namespace Scrum.Web

open System
open System.Collections.Generic
open System.Data.SQLite
open System.Diagnostics
open System.IO.Compression
open System.IdentityModel.Tokens.Jwt
open System.Reflection
open System.Security.Claims
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Mvc.Controllers
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open Microsoft.IdentityModel.JsonWebTokens
open Microsoft.IdentityModel.Tokens
open Microsoft.Net.Http.Headers
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.AspNetCore.ResponseCompression
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Application.DomainEventRequest
open Scrum.Infrastructure
open Scrum.Infrastructure.Seedwork.Json

module Seedwork =
    type ControllerWithinModule() =
        inherit ControllerFeatureProvider()

        override _.IsController(typeInfo: TypeInfo) : bool =
            // By default only a public top-level type ending in Controller is considered one.
            // It means controllers inside a module isn't found. A module compiles to a class
            // with nested classes for controllers.
            base.IsController(typeInfo)
            || typeInfo.FullName.StartsWith("Scrum.Web.Controller")

    module Json =
        // System.Text.Json cannot serialize an exception without itself throwing an exception:
        // System.NotSupportedException: Serialization and deserialization of 'System.Reflection.MethodBase' instances are not supported. Path: $.Result.Exception.TargetSite.
        // The converters works around the issue by limiting serialization to the most relevant parts of the exception.
        type ExceptionJsonConverter() =
            inherit JsonConverter<Exception>()
            override _.Read(_, _, _) = raise (UnreachableException())

            override x.Write(writer: Utf8JsonWriter, value: Exception, options: JsonSerializerOptions) =
                writer.WriteStartObject()
                writer.WriteString(nameof value.Message, value.Message)

                if value.InnerException <> null then
                    writer.WriteStartObject(nameof value.InnerException)
                    x.Write(writer, value.InnerException, options)
                    writer.WriteEndObject()

                if value.TargetSite <> null then
                    writer.WriteStartObject(nameof value.TargetSite)
                    writer.WriteString(nameof value.TargetSite.Name, value.TargetSite.Name)
                    writer.WriteString(nameof value.TargetSite.DeclaringType, value.TargetSite.DeclaringType.FullName)
                    writer.WriteEndObject()

                if value.StackTrace <> null then
                    writer.WriteString(nameof value.StackTrace, value.StackTrace)

                writer.WriteString(nameof Type, value.GetType().FullName)
                writer.WriteEndObject()

    // RFC7807 error format per https://opensource.zalando.com/restful-api-guidelines/#176.
    type ErrorDto = { Type: string; Title: string; Status: int; Detail: string }

    module ErrorDto =
        let create status detail : ErrorDto = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

        let toJsonResult (accept: StringValues) (error: ErrorDto) : ActionResult =
            let h = accept.ToArray() |> Array.exists (fun v -> v = "application/problem+json")
            JsonResult(error, StatusCode = error.Status, ContentType = (if h then "application/problem+json" else "application/json"))
            :> ActionResult

        let createJsonResult (accept: StringValues) status detail : ActionResult = create status detail |> toJsonResult accept

        type ValidationErrorDto = { Field: string; Message: string }

        let errorMessageSerializationOptions =
            JsonSerializerOptions(PropertyNamingPolicy = SnakeCaseLowerNamingPolicy())

        let fromValidationErrors (accept: StringValues) (errors: ValidationError list) : ActionResult =
            (errors |> List.map (fun e -> { Field = e.Field; Message = e.Message }), errorMessageSerializationOptions)
            |> JsonSerializer.Serialize
            |> createJsonResult accept StatusCodes.Status400BadRequest

        let fromUncaughtException (accept: StringValues) : ActionResult =
            createJsonResult accept StatusCodes.Status500InternalServerError "Internal server error"

module Configuration =
    type JwtAuthenticationOptions() =
        static member JwtAuthentication: string = nameof JwtAuthenticationOptions.JwtAuthentication
        member val Issuer: Uri = null with get, set
        member val Audience: Uri = null with get, set
        member val SigningKey: string = null with get, set
        member val ExpirationInSeconds: uint = 0ul with get, set

        member x.Validate() : unit =
            if isNull x.Issuer then
                nullArg (nameof x.Issuer)
            if isNull x.Audience then
                nullArg (nameof x.Audience)
            if String.IsNullOrWhiteSpace(x.SigningKey) then
                raise (ArgumentException(nameof x.SigningKey))
            if x.ExpirationInSeconds < 60ul then
                raise (ArgumentException(nameof x.ExpirationInSeconds))

open Configuration

module Service =
    module IdentityProviderService =
        let UserIdClaim = "userId"
        let RoleClaim = "role"

        let sign (clock: ISystemClock) (options: JwtAuthenticationOptions) (claims: Claim array) : string =
            let securityKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey))
            let credentials = SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
            let validUntilUtc = clock.CurrentUtc().AddSeconds(int options.ExpirationInSeconds)
            let token =
                JwtSecurityToken(
                    options.Issuer.ToString(),
                    options.Audience.ToString(),
                    claims,
                    expires = validUntilUtc,
                    signingCredentials = credentials
                )
            JwtSecurityTokenHandler().WriteToken(token)

        let issueToken (clock: ISystemClock) (options: JwtAuthenticationOptions) (userId: string) (role: string) =
            // With an actual user store, we'd validate the user here. Here we assume userId and role are valid.
            // Role must either be "regular" or "admin"
            // TODO: assert role with DU parse method or is that overkill?
            [| Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
               Claim(UserIdClaim, userId)
               Claim(RoleClaim, role) |]
            |> sign clock options

open Seedwork
open Service

module Controller =
    type ScrumController(configuration: IConfiguration) =
        inherit ControllerBase()

        let connectionString = configuration.GetConnectionString("Scrum")
        let env = new AppEnv(connectionString) :> IAppEnv

        member _.Env = env

        [<NonAction>]
        member this.HandleExceptionAsync (e: exn) (acceptHeaders: StringValues) (ct: CancellationToken) : Task<ActionResult> =
            task {
                this.Env.Logger.LogException(e)
                do! this.Env.RollbackAsync(ct)
                return ErrorDto.fromUncaughtException acceptHeaders
            }

        interface IDisposable with
            member this.Dispose() = this.Env.Dispose()

    type StoryCreateDto = { title: string; description: string }
    type StoryUpdateDto = { title: string; description: string }
    type AddTaskToStoryDto = { title: string; description: string }
    type StoryTaskUpdateDto = { title: string; description: string }

    [<ApiController>]
    [<Route("[controller]")>]
    type StoriesController(configuration: IConfiguration) =
        inherit ScrumController(configuration)

        // Success: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'
        // Failure: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": ""}' | jq

        [<HttpPost>]
        member x.CreateStory([<FromBody>] request: StoryCreateDto, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        CreateStoryCommand.runAsync
                            x.Env.StoryRepository
                            x.Env.SystemClock
                            x.Env.Logger
                            ct
                            { Id = Guid.NewGuid()
                              Title = request.title
                              Description = request.description |> Option.ofObj }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                        | Error e ->
                            match e with
                            | CreateStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | CreateStoryCommand.DuplicateStory id -> raise (UnreachableException(string id))
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e --insecure --request put -H 'Content-Type: application/json' -d '{"title": "title1","description": "description1"}'

        [<HttpPut>]
        [<Route("{id}")>]
        member x.UpdateStory([<FromBody>] request: StoryUpdateDto, id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        UpdateStoryCommand.runAsync
                            x.Env.StoryRepository
                            x.Env.SystemClock
                            x.Env.Logger
                            ct
                            { Id = id
                              Title = request.title
                              Description = request.description |> Option.ofObj }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                        | Error e ->
                            match e with
                            | UpdateStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | UpdateStoryCommand.StoryNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // curl https://localhost:5000/stories/fec32101-72b0-4d96-814f-de1c5b2dd140 --insecure --request delete

        [<HttpDelete>]
        [<Route("{id}")>]
        member x.DeleteStory(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = DeleteStoryCommand.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok _ -> OkResult() :> ActionResult
                        | Error e ->
                            match e with
                            | DeleteStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | DeleteStoryCommand.StoryNotFound _ ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks/57db7489-722f-4d66-97d5-d5c2501eb89e --insecure --request delete

        [<HttpDelete>]
        [<Route("{storyId}/tasks/{taskId}")>]
        member x.DeleteTaskFromStory(storyId: Guid, taskId: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = DeleteTaskCommand.runAsync x.Env.StoryRepository x.Env.Logger ct { StoryId = storyId; TaskId = taskId }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok _ -> OkResult() :> ActionResult
                        | Error e ->
                            match e with
                            | DeleteTaskCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | DeleteTaskCommand.StoryNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | DeleteTaskCommand.TaskNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // Success: curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'

        [<HttpPost>]
        [<Route("{storyId}/tasks")>]
        member x.AddTaskToStory([<FromBody>] request: AddTaskToStoryDto, storyId: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        AddTaskToStoryCommand.runAsync
                            x.Env.StoryRepository
                            x.Env.SystemClock
                            x.Env.Logger
                            ct
                            { TaskId = Guid.NewGuid()
                              StoryId = storyId
                              Title = request.title
                              Description = request.description |> Option.ofObj }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok taskId -> CreatedResult($"/stories/{storyId}/tasks/{taskId}", taskId) :> ActionResult
                        | Error e ->
                            match e with
                            | AddTaskToStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | AddTaskToStoryCommand.StoryNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | AddTaskToStoryCommand.DuplicateTask id -> raise (UnreachableException(string id))
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks/916397d3-0c10-495c-a6e3-a081d41f644c --insecure --request put -H 'Content-Type: application/json' -d '{"title": "title1","description": "description1"}'

        [<HttpPut>]
        [<Route("{storyId}/tasks/{taskId}")>]
        member x.UpdateTaskOnStory
            (
                [<FromBody>] request: StoryTaskUpdateDto,
                storyId: Guid,
                taskId: Guid,
                ct: CancellationToken
            ) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        UpdateTaskCommand.runAsync
                            x.Env.StoryRepository
                            x.Env.SystemClock
                            x.Env.Logger
                            ct
                            { StoryId = storyId
                              TaskId = taskId
                              Title = request.title
                              Description = request.description |> Option.ofObj }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok _ -> OkResult() :> ActionResult
                        | Error e ->
                            match e with
                            | UpdateTaskCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | UpdateTaskCommand.StoryNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | UpdateTaskCommand.TaskNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e --insecure | jq

        [<HttpGet>]
        [<Route("{id}")>]
        member x.GetByStoryId(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = GetStoryByIdQuery.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                    return
                        match result with
                        | Ok s -> OkObjectResult(s) :> ActionResult
                        | Error e ->
                            match e with
                            | GetStoryByIdQuery.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                            | GetStoryByIdQuery.StoryNotFound id ->
                                ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

    [<ApiController>] // TODO: Is this required or can it be on parent class only?
    // TODO: check zalando for dash in controller name
    [<Route("[controller]")>]
    type DomainEventsController(configuration: IConfiguration) =
        inherit ScrumController(configuration)

        // curl https://localhost:5000/domainEvents/15443e47-544a-477a-bc01-915ffd434ab6 --insecure | jq

        [<HttpGet>] // TODO: Combine HttpGet (and others with Route)
        [<Route("{id}")>]
        member x.GetEvents(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = GetByAggregateIdQuery.runAsync x.Env.DomainEventRepository x.Env.Logger ct { Id = id }
                    return
                        match result with
                        | Ok s -> OkObjectResult(s) :> ActionResult
                        | Error e ->
                            match e with
                            | GetByAggregateIdQuery.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

    // As the token is supposed to be opaque, we could either expose some of the claims in
    // next to token or have clients call the introspect endpoint. We chose the latter.
    type AuthenticationResponse = { Token: string }

    [<ApiController>] // TODO: Is this required or can it be on parent class only?
    // TODO: check zalando for dash in controller name
    [<Route("[controller]")>]
    type AuthenticationController
        (
            configuration: IConfiguration,
            jwtAuthenticationOptions: IOptions<JwtAuthenticationOptions>,
            httpContextAccessor: IHttpContextAccessor
        ) =
        inherit ScrumController(configuration)

        // Loosely modelled after OAuth authentication.

        // curl "https://localhost:5000/authentication/issueToken?userId=1&role=regular" --insecure --request post | jq

        [<HttpPost("issueToken")>]
        member x.IssueToken(userId: string, role: string) : Task<ActionResult> =
            task {
                // Get user from hypothetical user store and pass to issueRegularToken
                // to include information about the user as claims in the token.
                let token =
                    IdentityProviderService.issueToken x.Env.SystemClock jwtAuthenticationOptions.Value userId role
                return CreatedResult("/authentication/introspect", { Token = token })
            }

        // curl https://localhost:5000/authentication/introspect --insecure --request post -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiJkMTM4NjNhOC1kNDExLTRlOWItYTliYi01ZWRmZDJiOGYwNjEiLCJ1c2VySWQiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDEiLCJyb2xlIjoicmVndWxhciIsImV4cCI6MTY5NjQyOTAzOSwiaXNzIjoiaHR0cHM6Ly9zY3J1bS1kZXYvIiwiYXVkIjoiaHR0cHM6Ly9zY3J1bS1kZXYvIn0.x5DE_A5rvQUkb7UED4Ook8pBm5hpRmRauPNzpuvTQM0" | jq

        [<HttpPost("introspect")>]
        [<Authorize>]
        member _.Introspect() : IDictionary<string, obj> =
            let claimsPrincipal = httpContextAccessor.HttpContext.User
            let claimsIdentity = claimsPrincipal.Identity :?> ClaimsIdentity
            claimsIdentity.Claims
            |> Seq.map (fun c ->
                match c.Type, c.Value with
                | "exp" as t, v ->
                    // Special case non-string value or it becomes a string in string in
                    // the HTTP response.
                    t, Int32.Parse(v) :> obj
                | ClaimTypes.Role, v -> IdentityProviderService.RoleClaim, v :> obj
                | _ -> c.Type, c.Value :> obj)
            |> dict

module HealthCheck =
    type MemoryHealthCheck(allocatedThresholdInMb: int64) =
        interface IHealthCheck with
            member _.CheckHealthAsync(_, _) : Task<HealthCheckResult> =
                task {
                    // TODO: Use units of measure
                    let mb = 1024 * 1024
                    let allocatedInBytes = GC.GetTotalMemory(forceFullCollection = false)
                    let committedInBytes = GC.GetGCMemoryInfo().TotalCommittedBytes
                    let data = Dictionary<string, obj>()
                    data.Add("allocated_megabytes", Math.Round(float allocatedInBytes / float mb, 2))
                    data.Add("committed_megabytes", Math.Round(float committedInBytes / float mb, 2))
                    data.Add("gen0_collection_count", GC.CollectionCount(0))
                    data.Add("gen1_collection_count", GC.CollectionCount(1))
                    data.Add("gen2_collection_count", GC.CollectionCount(2))
                    return
                        HealthCheckResult(
                            (if allocatedInBytes < allocatedThresholdInMb * int64 mb then
                                 HealthStatus.Healthy
                             else
                                 HealthStatus.Degraded),
                            $"Reports degraded status if process has allocated >= {allocatedThresholdInMb} MB",
                            null,
                            data
                        )
                }

    type SQLiteHealthCheck(connectionString: string) =
        let description = "Reports unhealthy status if SQLite is unavailable"
        interface IHealthCheck with
            member _.CheckHealthAsync(_, ct) : Task<HealthCheckResult> =
                task {
                    try
                        // TODO: implementing timing helper
                        let sw = Stopwatch()
                        sw.Start()
                        use connection = new SQLiteConnection(connectionString)
                        do! connection.OpenAsync(ct)
                        use cmd = new SQLiteCommand("select 1", connection)
                        let! _ = cmd.ExecuteScalarAsync(ct)
                        sw.Stop()
                        let data = Dictionary<string, obj>()
                        data.Add("response_time_milliseconds", sw.ElapsedMilliseconds)
                        return HealthCheckResult(HealthStatus.Healthy, description, null, data)
                    with e ->
                        return HealthCheckResult(HealthStatus.Unhealthy, description, e, null)
                }

open HealthCheck

type Startup(configuration: IConfiguration) =
    // This method gets called by the runtime. Use this method to add services
    // to the container. For more information on how to configure your
    // application, visit https://go.microsoft.com/fwlink/?LinkID=398940
    member _.ConfigureServices(services: IServiceCollection) : unit =
        services.Configure<JwtAuthenticationOptions>(configuration.GetSection(JwtAuthenticationOptions.JwtAuthentication))
        |> ignore
        let serviceProvider = services.BuildServiceProvider()
        let jwtAuthenticationOptions =
            serviceProvider.GetService<IOptions<JwtAuthenticationOptions>>().Value
        jwtAuthenticationOptions.Validate()

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(fun options ->
                options.TokenValidationParameters <-
                    TokenValidationParameters(
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtAuthenticationOptions.Issuer.ToString(),
                        ValidAudience = jwtAuthenticationOptions.Audience.ToString(),
                        ClockSkew = TimeSpan.Zero,
                        IssuerSigningKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtAuthenticationOptions.SigningKey))
                    )

                // Leave in callbacks for troubleshooting JWT issues. Set a breakpoint on the
                // relevant lines below to inspect the JWT authentication process.
                options.Events <-
                    JwtBearerEvents(
                        OnAuthenticationFailed = (fun _ -> Task.CompletedTask),
                        OnTokenValidated = (fun _ -> Task.CompletedTask),
                        OnForbidden = (fun _ -> Task.CompletedTask),
                        OnChallenge = (fun _ -> Task.CompletedTask)
                    ))
        |> ignore

        // TODO: read from appsettings?
        // TODO: options.Conventions.Add(new RouteTokenTransformerConvention(new CamelCaseTransformer()));
        services.AddCors(fun options ->
            options.AddDefaultPolicy(fun builder -> builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod() |> ignore))
        |> ignore

        services.AddHttpContextAccessor() |> ignore

        services
            .AddMvc(fun options -> options.EnableEndpointRouting <- false)
            .ConfigureApplicationPartManager(fun pm -> pm.FeatureProviders.Add(ControllerWithinModule()))
            .AddJsonOptions(fun options ->
                let o = options.JsonSerializerOptions
                // Per https://opensource.zalando.com/restful-api-guidelines/#118.
                o.PropertyNamingPolicy <- SnakeCaseLowerNamingPolicy()
                // Per https://opensource.zalando.com/restful-api-guidelines/#169.
                o.Converters.Add(DateTimeJsonConverter())
                // Per https://opensource.zalando.com/restful-api-guidelines/#240.
                o.Converters.Add(EnumJsonConverter())
                o.WriteIndented <- true)
        |> ignore

        services
            .AddHealthChecks()
            .AddTypeActivatedCheck<MemoryHealthCheck>("Memory", HealthStatus.Degraded, Seq.empty, args = [| int64 (5 * 1024) |])
            .AddTypeActivatedCheck<SQLiteHealthCheck>(
                "Database",
                HealthStatus.Degraded,
                Seq.empty,
                args = [| configuration.GetConnectionString("Scrum") |]
            )
        |> ignore

        services.AddControllers() |> ignore
        services.AddResponseCaching() |> ignore
        services.AddEndpointsApiExplorer() |> ignore

        // Azure hosting under a Linux based app means the API is running a container.
        // Inside the container, the API is run using the dotnet command, meaning Kestrel is serving
        // traffic. Kestrel doesn't have build-in compression which is why the API is doing the compression:
        // https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression.
        services
            .AddResponseCompression(fun options ->
                options.EnableForHttps <- true
                options.Providers.Add<GzipCompressionProvider>())
            .Configure<GzipCompressionProviderOptions>(fun (options: GzipCompressionProviderOptions) ->
                options.Level <- CompressionLevel.SmallestSize)
            .Configure<BrotliCompressionProviderOptions>(fun (options: BrotliCompressionProviderOptions) ->
                options.Level <- CompressionLevel.SmallestSize)
        |> ignore

    // This method gets called by the runtime. Use this method to configure the
    // HTTP request pipeline.
    member _.Configure (app: IApplicationBuilder) (env: IWebHostEnvironment) : unit =
        if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore else ()

        app.UseHttpsRedirection() |> ignore
        app.UseCors() |> ignore

        // Per https://opensource.zalando.com/restful-api-guidelines/#227 and
        // https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware
        app.UseResponseCaching() |> ignore
        app.Use(fun context (next: RequestDelegate) ->
            task {
                let r = context.Response
                r.GetTypedHeaders().CacheControl <-
                    CacheControlHeaderValue(MustRevalidate = true, MaxAge = TimeSpan.FromSeconds(0), NoCache = true, NoStore = true)
                r.Headers[HeaderNames.Vary] <- [| "Accept, Accept-Encoding" |] |> StringValues.op_Implicit
                return! next.Invoke(context)
            }
            :> Task)
        |> ignore

        let healthCheckOptions =
            let jsonOptions =
                JsonSerializerOptions(PropertyNamingPolicy = SnakeCaseLowerNamingPolicy(), WriteIndented = true)
            jsonOptions.Converters.Add(Json.ExceptionJsonConverter())
            HealthCheckOptions(
                ResponseWriter =
                    fun context report ->
                        task {
                            context.Response.ContentType <- "application/json; charset=utf-8"
                            let result =
                                JsonSerializer.Serialize(
                                    {| Status = report.Status.ToString()
                                       Result =
                                        report.Entries
                                        |> Seq.map (fun e ->
                                            {| Key = e.Key
                                               Value = e.Value.Status.ToString()
                                               Description = e.Value.Description
                                               Data = e.Value.Data
                                               Exception = e.Value.Exception |}) |},
                                    jsonOptions
                                )
                            return! context.Response.WriteAsync(result)
                        }
            )

        // curl https://localhost:5000/health --insecure | jq
        app.UseHealthChecks("/health", healthCheckOptions) |> ignore
        app.UseResponseCompression() |> ignore
        app.UseRouting() |> ignore
        app.UseAuthentication() |> ignore
        app.UseAuthorization() |> ignore
        app.UseMvcWithDefaultRoute() |> ignore

module Program =
    let createHostBuilder args : IHostBuilder =
        Host
            .CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(fun builder -> builder.UseStartup<Startup>() |> ignore)

    [<EntryPoint>]
    let main args =
        // Short-hand initialization. .NET 7 moved away from Configure and ConfigureServices, but still support those.
        // - https://learn.microsoft.com/en-us/aspnet/core/migration/50-to-60-samples?view=aspnetcore-7.0
        // - https://mobiletonster.com/blog/code/aspnet-core-6-how-to-deal-with-the-missing-startupcs-file
        //
        // let builder = WebApplication.CreateBuilder(args)
        // builder.Services.AddControllers() |> ignore
        // let app = builder.Build()
        // app.UseHttpsRedirection() |> ignore
        // app.MapControllers() |> ignore
        // app.Run()

        let host = createHostBuilder(args).Build()
        host.Run()
        0
