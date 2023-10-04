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
    // By default only a public top-level type ending in Controller is
    // considered one. It means controllers inside a module isn't found. A
    // module compiles to a class with nested classes for controllers.
    type ControllerWithinModule() =
        inherit ControllerFeatureProvider()

        override _.IsController(typeInfo: TypeInfo) : bool =
            base.IsController(typeInfo)
            || typeInfo.FullName.StartsWith("Scrum.Web.Controller")

    module Json =
        // System.Text.Json cannot serialize an exception without itself
        // throwing an exception: "System.NotSupportedException: Serialization
        // and deserialization of 'System.Reflection.MethodBase' instances are
        // not supported. Path: $.Result.Exception.TargetSite.". The converters
        // works around the issue by limiting serialization to the most relevant
        // parts of the exception.
        type ExceptionJsonConverter() =
            inherit JsonConverter<Exception>()
            
            override _.Read(_, _, _) = raise (UnreachableException())

            override x.Write(writer: Utf8JsonWriter, value: Exception, options: JsonSerializerOptions) =
                writer.WriteStartObject()
                writer.WriteString(nameof value.Message, value.Message)

                if not (isNull value.InnerException) then
                    writer.WriteStartObject(nameof value.InnerException)
                    x.Write(writer, value.InnerException, options)
                    writer.WriteEndObject()

                if not (isNull value.TargetSite) then
                    writer.WriteStartObject(nameof value.TargetSite)
                    writer.WriteString(nameof value.TargetSite.Name, value.TargetSite.Name)
                    writer.WriteString(nameof value.TargetSite.DeclaringType, value.TargetSite.DeclaringType.FullName)
                    writer.WriteEndObject()

                if not (isNull value.StackTrace) then
                    writer.WriteString(nameof value.StackTrace, value.StackTrace)

                writer.WriteString(nameof Type, value.GetType().FullName)
                writer.WriteEndObject()

    // RFC7807 problem detail format per
    // https://opensource.zalando.com/restful-api-guidelines/#176.
    type ProblemDetail = { Type: string; Title: string; Status: int; Detail: string }

    module ProblemDetail =
        let create status detail : ProblemDetail = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

        let toJsonResult (accept: StringValues) (error: ProblemDetail) : ActionResult =
            let h = accept.ToArray() |> Array.exists (fun v -> v = "application/problem+json")
            JsonResult(error, StatusCode = error.Status, ContentType = (if h then "application/problem+json" else "application/json"))
            :> ActionResult

        let createJsonResult (accept: StringValues) status detail : ActionResult = create status detail |> toJsonResult accept

        let createAuthorizationError (accept: StringValues) (message: string) : ActionResult =
            createJsonResult accept StatusCodes.Status401Unauthorized message

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
    // Names of claims shared between services.
    module ScrumClaims =
        let UserIdClaim = "userId"
        let RolesClaim = "roles"

    // Web specific implementation of IUserIdentityService so it belongs in
    // Program.fs rather than Infrastructure.fs.
    type UserIdentity(context: HttpContext) =
        interface IUserIdentity with
            member x.GetCurrent() : ScrumIdentity =
                // Access to HttpContext from outside a controller goes through
                // IHttpContextAccess per
                // https://docs.microsoft.com/en-us/aspnet/core/migration/claimsprincipal-current.
                // Running in a non-HTTP context, HttpContext is therefore null.
                if isNull context then
                    Anonymous
                else
                    let claimsPrincipal = context.User
                    if isNull claimsPrincipal then
                        Anonymous
                    else
                        let claimsIdentity = context.User.Identity :?> ClaimsIdentity
                        let claims = claimsIdentity.Claims
                        if Seq.isEmpty claims then
                            Anonymous
                        else
                            let userIdClaim =
                                claims
                                |> Seq.filter (fun c -> c.Type = ScrumClaims.UserIdClaim)
                                |> Seq.map (fun c -> c.Value)
                                |> Seq.exactlyOne
                            let rolesClaim =
                                claims
                                |> Seq.filter (fun c -> c.Type = ClaimTypes.Role)
                                |> Seq.map (fun c -> ScrumRole.FromString(c.Value))
                                |> List.ofSeq

                            // With a proper identity provider, it's likely we'd
                            // have more kinds of authenticated identities, and
                            // that we'd use a claim's value to determine which
                            // one.
                            match List.length rolesClaim with
                            | 0 -> Anonymous
                            | _ -> Authenticated(userIdClaim, rolesClaim)

    // IUserIdentity is defined in Application.fs because application code needs
    // to consult the current identity as part of running use cases.
    // IdentityProviderService, on the other hand, is of no concern to the
    // application layer and is host dependent. AppEnv will never have to
    // resolve IIdentityProviderServer service. Therefore, we couldn't
    // implemented the service logic inside the Authentication controller, but
    // to keep controllers lean, we extract the logic into a separate class
    type IdentityProvider(clock: ISystemClock, options: JwtAuthenticationOptions) =
        let sign (claims: Claim array) : string =
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

        member _.IssueToken (userId: string) (roles: ScrumRole list) : string =
            // With an actual user store, we'd validate user credentials here.
            // But for this app, userId may be any string and role must be
            // either "regular" or "admin"
            let roles =
                roles
                |> List.map (fun r -> Claim(ScrumClaims.RolesClaim, r.ToString()))
                |> List.toArray
            let rest =
                [| Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
                   Claim(ScrumClaims.UserIdClaim, userId) |]
            Array.concat [ roles; rest ] |> sign

        member x.RenewToken(identity: ScrumIdentity) : Result<string, string> =
            match identity with
            | Anonymous -> Error "User is anonymous"
            | Authenticated(id, roles) -> Ok(x.IssueToken id roles)

open Seedwork
open Service

module Controller =
    type ScrumController(configuration: IConfiguration, httpContext: IHttpContextAccessor) =
        inherit ControllerBase()

        let connectionString = configuration.GetConnectionString("Scrum")
        let userIdentityService = UserIdentity(httpContext.HttpContext)
        let env = new AppEnv(connectionString, userIdentityService) :> IAppEnv

        member _.Env = env

        [<NonAction>]
        member x.HandleExceptionAsync (e: exn) (acceptHeaders: StringValues) (ct: CancellationToken) : Task<ActionResult> =
            task {
                x.Env.Logger.LogException(e)
                do! x.Env.RollbackAsync(ct)
                return ProblemDetail.fromUncaughtException acceptHeaders
            }

        interface IDisposable with
            member x.Dispose() = x.Env.Dispose()

    // As the token is supposed to be opaque, we can either expose information
    // from claims inside the token as additional fields or provide clients with
    // an introspect endpoint. We chose the latter.
    type AuthenticationResponse = { Token: string }

    // Loosely modeled after the corresponding OAuth2 endpoint.
    [<Route("[controller]")>]
    type AuthenticationController
        (configuration: IConfiguration, httpContext: IHttpContextAccessor, jwtAuthenticationOptions: IOptions<JwtAuthenticationOptions>) as x
        =
        inherit ScrumController(configuration, httpContext)

        let idp = IdentityProvider(x.Env.SystemClock, jwtAuthenticationOptions.Value)

        [<HttpPost("issue-token")>]
        member _.IssueToken(userId: string, roles: string) : Task<ActionResult> =
            task {
                // Get user from hypothetical user store and pass to issueRegularToken
                // to include information about the user as claims in the token.
                let roles = roles.Split(',') |> Array.map ScrumRole.FromString |> Array.toList
                let token = idp.IssueToken userId roles
                return CreatedResult("/authentication/introspect", { Token = token })
            }

        [<Authorize; HttpPost("renewToken")>]
        member _.RenewToken() : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                let identity = x.Env.UserIdentity.GetCurrent()
                let token = idp.RenewToken identity
                return
                    (match token with
                     | Ok token -> CreatedResult("/authentication/introspect", { Token = token }) :> ActionResult
                     | Error e -> ProblemDetail.createJsonResult accept StatusCodes.Status400BadRequest e)
            }

        [<Authorize; HttpPost("introspect")>]
        member _.Introspect() : IDictionary<string, obj> =
            let claimsPrincipal = x.HttpContext.User
            let claimsIdentity = claimsPrincipal.Identity :?> ClaimsIdentity
            let map = Dictionary<string, obj>()

            for c in claimsIdentity.Claims do
                // Special case non-string value or it becomes a string in
                // string in the HTTP response.
                if c.Type = "exp" then
                    map.Add("exp", Int32.Parse(c.Value) :> obj)
                elif c.Type = ClaimTypes.Role then
                    // For reasons unknown, ASP.NET maps our Scrum RoleClaim
                    // from the bearer token to ClaimTypes.Role. The claim's
                    // type deserialized becomes
                    // http://schemas.microsoft.com/ws/2008/06/identity/claims/role.
                    let ok, values = map.TryGetValue(ScrumClaims.RolesClaim)
                    if ok then
                        let values = values :?> ResizeArray<string>
                        values.Add(c.Value)
                    else
                        map.Add(ScrumClaims.RolesClaim, [ c.Value ] |> ResizeArray)
                else
                    map.Add(c.Type, c.Value)

            map

    type StoryCreateDto = { title: string; description: string }
    type StoryUpdateDto = { title: string; description: string }
    type AddTaskToStoryDto = { title: string; description: string }
    type StoryTaskUpdateDto = { title: string; description: string }

    [<Authorize; Route("[controller]")>]
    type StoriesController(configuration: IConfiguration, httpContext: IHttpContextAccessor) =
        inherit ScrumController(configuration, httpContext)

        [<HttpPost>]
        member x.CreateStory([<FromBody>] request: StoryCreateDto, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        CreateStoryCommand.runAsync
                            x.Env.UserIdentity
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
                            | CreateStoryCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | CreateStoryCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | CreateStoryCommand.DuplicateStory id -> raise (UnreachableException(string id))
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpPut("{id}")>]
        member x.UpdateStory([<FromBody>] request: StoryUpdateDto, id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        UpdateStoryCommand.runAsync
                            x.Env.UserIdentity
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
                            | UpdateStoryCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | UpdateStoryCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | UpdateStoryCommand.StoryNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpPost("{storyId}/tasks")>]
        member x.AddTaskToStory([<FromBody>] request: AddTaskToStoryDto, storyId: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        AddTaskToStoryCommand.runAsync
                            x.Env.UserIdentity
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
                            | AddTaskToStoryCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | AddTaskToStoryCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | AddTaskToStoryCommand.StoryNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | AddTaskToStoryCommand.DuplicateTask id -> raise (UnreachableException(string id))
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpPut("{storyId}/tasks/{taskId}")>]
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
                            x.Env.UserIdentity
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
                            | UpdateTaskCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | UpdateTaskCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | UpdateTaskCommand.StoryNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | UpdateTaskCommand.TaskNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpDelete("{storyId}/tasks/{taskId}")>]
        member x.DeleteTaskFromStory(storyId: Guid, taskId: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result =
                        DeleteTaskCommand.runAsync
                            x.Env.UserIdentity
                            x.Env.StoryRepository
                            x.Env.Logger
                            ct
                            { StoryId = storyId; TaskId = taskId }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok _ -> OkResult() :> ActionResult
                        | Error e ->
                            match e with
                            | DeleteTaskCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | DeleteTaskCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | DeleteTaskCommand.StoryNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                            | DeleteTaskCommand.TaskNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpDelete("{id}")>]
        member x.DeleteStory(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = DeleteStoryCommand.runAsync x.Env.UserIdentity x.Env.StoryRepository x.Env.Logger ct { Id = id }
                    do! x.Env.CommitAsync(ct)
                    return
                        match result with
                        | Ok _ -> OkResult() :> ActionResult
                        | Error e ->
                            match e with
                            | DeleteStoryCommand.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | DeleteStoryCommand.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | DeleteStoryCommand.StoryNotFound _ ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

        [<HttpGet("{id}")>]
        member x.GetByStoryId(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = GetStoryByIdQuery.runAsync x.Env.UserIdentity x.Env.StoryRepository x.Env.Logger ct { Id = id }
                    return
                        match result with
                        | Ok s -> OkObjectResult(s) :> ActionResult
                        | Error e ->
                            match e with
                            | GetStoryByIdQuery.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | GetStoryByIdQuery.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                            | GetStoryByIdQuery.StoryNotFound id ->
                                ProblemDetail.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

    [<Authorize; Route("persisted-domain-events")>]
    type PersistedDomainEventsController(configuration: IConfiguration, httpContext: IHttpContextAccessor) =
        inherit ScrumController(configuration, httpContext)

        [<HttpGet("{id}")>]
        member x.GetPersistedDomainEvents(id: Guid, ct: CancellationToken) : Task<ActionResult> =
            task {
                let accept = x.Request.Headers.Accept
                try
                    let! result = GetByAggregateIdQuery.runAsync x.Env.UserIdentity x.Env.DomainEventRepository x.Env.Logger ct { Id = id }
                    return
                        match result with
                        | Ok s -> OkObjectResult(s) :> ActionResult
                        | Error e ->
                            match e with
                            | GetByAggregateIdQuery.AuthorizationError ae -> ProblemDetail.createAuthorizationError accept ae
                            | GetByAggregateIdQuery.ValidationErrors ve -> ProblemDetail.fromValidationErrors accept ve
                with e ->
                    return! x.HandleExceptionAsync e accept ct
            }

module HealthCheck =
    type MemoryHealthCheck(allocatedThresholdInMb: int64) =
        let mb = 1024 * 2024
        interface IHealthCheck with
            member _.CheckHealthAsync(_, _) : Task<HealthCheckResult> =
                task {
                    let allocatedBytes = GC.GetTotalMemory(forceFullCollection = false)
                    let committedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes
                    let data = Dictionary<string, obj>()
                    data.Add("allocated_megabytes", Math.Round(float allocatedBytes / float mb, 2))
                    data.Add("committed_megabytes", Math.Round(float committedBytes / float mb, 2))
                    data.Add("gen0_collection_count", GC.CollectionCount(0))
                    data.Add("gen1_collection_count", GC.CollectionCount(1))
                    data.Add("gen2_collection_count", GC.CollectionCount(2))
                    return
                        HealthCheckResult(
                            (if allocatedBytes < allocatedThresholdInMb * int64 mb then
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
                        let _, elapsed =
                            time (fun _ ->
                                task {
                                    use connection = new SQLiteConnection(connectionString)
                                    do! connection.OpenAsync(ct)
                                    use cmd = new SQLiteCommand("select 1", connection)
                                    let! _ = cmd.ExecuteScalarAsync(ct)
                                    return ()
                                })
                        let data = Dictionary<string, obj>()
                        data.Add("response_time_milliseconds", elapsed)
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

                // Leave in callbacks for troubleshooting JWT issues. Set a
                // breakpoint on the relevant lines below to inspect the JWT
                // authentication process.
                options.Events <-
                    JwtBearerEvents(
                        OnAuthenticationFailed = (fun _ -> Task.CompletedTask),
                        OnTokenValidated = (fun _ -> Task.CompletedTask),
                        OnForbidden = (fun _ -> Task.CompletedTask),
                        OnChallenge = (fun _ -> Task.CompletedTask)
                    ))
        |> ignore

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

        // Azure hosting under a Linux based app means the API is running a
        // container. Inside the container, the API is run using the dotnet
        // command, meaning Kestrel is serving traffic. Kestrel doesn't have
        // build-in compression which is why the API is doing the compression:
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
        // https://social.msdn.microsoft.com/Forums/vstudio/en-US/bcb2b3fa-9fcd-4a90-9f9c-9ef24332451e/how-to-handle-exceptions-with-taskschedulerunobservedtaskexception?forum=parallelextensions
        TaskScheduler.UnobservedTaskException.Add(fun (e: UnobservedTaskExceptionEventArgs) ->
            e.SetObserved()
            e.Exception.Handle(fun e ->
                printfn $"Unobserved %s{e.GetType().Name}: %s{e.Message}. %s{e.StackTrace}"
                true))

        let host = createHostBuilder(args).Build()
        host.Run()
        0
