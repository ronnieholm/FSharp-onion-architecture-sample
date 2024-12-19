namespace Scrum.WebHost

module HealthCheck =
    open System
    open System.Collections.Generic
    open System.Data.SQLite
    open System.Threading.Tasks
    open Microsoft.Extensions.Diagnostics.HealthChecks
    open Scrum.Shared.Application.Seedwork

    type MemoryHealthCheck(allocatedThresholdInMb: int64) =
        let mb = 1024 * 2024
        interface IHealthCheck with
            member _.CheckHealthAsync(_, _) =
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

module Filter =
    open System.Diagnostics
    open System.Net
    open Microsoft.AspNetCore.Mvc.Filters
    open Microsoft.Extensions.Hosting
    open Microsoft.AspNetCore.Mvc
    open Scrum.Shared.Infrastructure.Seedwork

    type WebExceptionFilterAttribute(hostEnvironment: IHostEnvironment) =
        inherit ExceptionFilterAttribute()

        override _.OnException context =
            if hostEnvironment.IsDevelopment() then
                ()
            else
                let traceId = context.HttpContext.TraceIdentifier
                let activityId = Activity.Current.Id
                let code, message =
                    match context.Exception with
                    // If needed, adjust HTTP status code and message based on
                    // exception type.
                    | :? InfrastructureException as _
                    | :? WebException as _
                    | _ -> HttpStatusCode.InternalServerError, $"Internal Server Error (ActivityId: {activityId}, TraceId: {traceId})"

                let code = LanguagePrimitives.EnumToValue code
                context.HttpContext.Response.ContentType <- ProblemDetails.inferContentType context.HttpContext.Request.Headers.Accept
                context.HttpContext.Response.StatusCode <- code
                context.Result <- JsonResult(ProblemDetails.create code message)

module RouteHandler =
    open System
    open System.Security.Claims
    open System.Collections.Generic
    open System.Text.Json
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.Options
    open Giraffe
    open FsToolkit.ErrorHandling
    open Scrum.Shared.Infrastructure
    open Scrum.Shared.Application.Seedwork
    open Scrum.Shared.Infrastructure.Seedwork
    open Scrum.Shared.Infrastructure.Configuration
    open Scrum.Shared.Infrastructure.Service
    open Scrum.Shared.RouteHandler
    open Scrum.Story.RouteHandler

    let errorMessageSerializationOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

    let verifyUserIsAuthenticated: HttpHandler =
        // TODO: this function comes part of Giraffe.
        fun (next: HttpFunc) (ctx: HttpContext) ->
            if isNotNull ctx.User && ctx.User.Identity.IsAuthenticated then
                next ctx
            else
                setStatusCode 401 earlyReturn ctx

    let issueTokenHandler: HttpHandler =
        // TODO: Fail if token is provided and/or user isn't anonymous
        fun (next: HttpFunc) (ctx: HttpContext) ->
            let settings = ctx.GetService<IOptions<JwtAuthenticationSettings>>()
            let settings = settings.Value
            let response =
                result {
                    let! userId =
                        ctx.GetQueryStringValue "userId"
                        |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "userId")
                    let! roles =
                        ctx.GetQueryStringValue "roles"
                        |> Result.map (fun r -> r.Split(',') |> Array.map ScrumRole.fromString |> Array.toList)
                        |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "roles")
                    let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof userId; nameof roles ]
                    let token = IdentityProvider.issueToken settings DateTime.UtcNow userId roles

                    // As the token is opaque, we can either promote information
                    // from inside the token to fields on the response object or
                    // provide clients with an introspect endpoint. We chose the
                    // latter while still wrapping the token in a response.
                    return {| Token = token |}
                }

            match response with
            | Ok r ->
                ctx.SetStatusCode 201
                ctx.SetHttpHeader("Location", "/authentication/introspect")
                json r next ctx
            | Error e ->
                ctx.SetStatusCode 400
                ctx.SetContentType(ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                json e next ctx

    let renewTokenHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            let settings = ctx.GetService<IOptions<JwtAuthenticationSettings>>()
            let settings = settings.Value
            let identity = UserIdentity.getCurrentIdentity ctx
            let result = IdentityProvider.renewToken settings DateTime.UtcNow identity
            match result with
            | Ok token ->
                ctx.SetStatusCode 201
                ctx.SetHttpHeader("Location", "/authentication/introspect")
                json {| Token = token |} next ctx
            | Error e ->
                ctx.SetStatusCode 400
                ctx.SetContentType(ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                json e next ctx

    let introspectTokenHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            let claimsPrincipal = ctx.User
            let claimsIdentity = claimsPrincipal.Identity :?> ClaimsIdentity
            let map = Dictionary<string, obj>()

            for c in claimsIdentity.Claims do
                // Special case non-string value, or it becomes a string in the
                // JSON rendering of the claim.
                if c.Type = "exp" then
                    map.Add("exp", Int32.Parse(c.Value) :> obj)
                elif c.Type = ClaimTypes.Role then
                    // For reasons unknown, ASP.NET maps our Scrum RoleClaim
                    // from the bearer token to ClaimTypes.Role. The claim's
                    // type JSON rendered would become
                    // http://schemas.microsoft.com/ws/2008/06/identity/claims/role.
                    let ok, values = map.TryGetValue(ScrumClaims.RolesClaim)
                    if ok then
                        let values = values :?> ResizeArray<string>
                        values.Add(c.Value)
                    else
                        map.Add(ScrumClaims.RolesClaim, [ c.Value ] |> ResizeArray)
                else
                    map.Add(c.Type, c.Value)

            json map next ctx

    let introspectTestHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            // API gateways and other proxies between the client and the service
            // tag on extra information to the request. This endpoint allows a
            // client to see what the request looked like from the server's
            // point of view.
            let headers =
                ctx.Request.Headers |> Seq.map (fun h -> KeyValuePair(h.Key, string h.Value))
            ctx.SetStatusCode 200
            json headers next ctx

    let currentTimeTestHandler: HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            // Useful for establishing baseline performance numbers and testing
            // rate limits. Because this action doesn't perform significant
            // work, it provides an upper bound for requests/second given a
            // response time distribution.
            text (string DateTime.UtcNow) next ctx

    let webApp =
        choose
            [
              // Loosely modeled after OAuth2 endpoints.
              POST
              >=> choose
                  [ route "/authentication/issue-token" >=> issueTokenHandler
                    route "/authentication/renew-token"
                    >=> verifyUserIsAuthenticated
                    >=> renewTokenHandler
                    route "/authentication/introspect"
                    >=> verifyUserIsAuthenticated
                    >=> introspectTokenHandler ]

              // Could be included in Story.fs, but kept in Program.fs because
              // having endpoints defined in a central location helps understand
              // the app and makes it easier to spot API inconsistencies.
              verifyUserIsAuthenticated
              >=> choose
                  [ POST >=> route "/stories" >=> CaptureBasicStoryDetails.handle
                    PUT >=> routef "/stories/%O" ReviseBasicStoryDetails.handle
                    POST >=> routef "/stories/%O/tasks" AddBasicTaskDetailsToStory.handle
                    PUT >=> routef "/stories/%O/tasks/%O" ReviseBasicTaskDetails.handle
                    DELETE >=> routef "/stories/%O/tasks/%O" RemoveTask.handle
                    DELETE >=> routef "/stories/%O" RemoveStory.handle
                    GET >=> routef "/stories/%O" GetStoryById.handle
                    GET >=> route "/stories" >=> GetStoriesPaged.handle ]

              verifyUserIsAuthenticated
              >=> choose [ GET >=> routef "/persisted-domain-events/%O" GetPersistedDomainEvents.handle ]

              GET
              >=> choose
                  [ route "/tests/introspect" >=> introspectTestHandler
                    route "/tests/current-time" >=> currentTimeTestHandler ]

              RequestErrors.NOT_FOUND "Not Found" ]

module Program =
    open System
    open System.Globalization
    open System.IO.Compression
    open System.Text
    open System.Text.Json
    open System.Threading.Tasks
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open Microsoft.AspNetCore.Authentication.JwtBearer
    open Microsoft.Extensions.Configuration
    open Microsoft.Extensions.Hosting
    open Microsoft.Extensions.DependencyInjection
    open Microsoft.Extensions.Logging
    open Microsoft.Extensions.Options
    open Microsoft.Extensions.Primitives
    open Microsoft.IdentityModel.Tokens
    open Microsoft.Net.Http.Headers
    open Microsoft.AspNetCore.Diagnostics.HealthChecks
    open Microsoft.Extensions.Diagnostics.HealthChecks
    open Microsoft.AspNetCore.ResponseCompression
    open Giraffe
    open Scrum.Shared.Infrastructure    
    open Scrum.Shared.Infrastructure.Seedwork.Json
    open Scrum.Shared.Infrastructure.Configuration
    open Seedwork
    open HealthCheck
    open Filter

    // Avoid the application using the host's (unexpected) culture. This can
    // make parsing unexpectedly go wrong.
    CultureInfo.DefaultThreadCurrentCulture <- CultureInfo.InvariantCulture
    CultureInfo.DefaultThreadCurrentUICulture <- CultureInfo.InvariantCulture

    // Top-level handler for unobserved task exceptions
    // https://social.msdn.microsoft.com/Forums/vstudio/en-US/bcb2b3fa-9fcd-4a90-9f9c-9ef24332451e/how-to-handle-exceptions-with-taskschedulerunobservedtaskexception?forum=parallelextensions
    TaskScheduler.UnobservedTaskException.Add(fun (e: UnobservedTaskExceptionEventArgs) ->
        e.SetObserved()
        e.Exception.Handle(fun e ->
            printfn $"Unobserved %s{e.GetType().Name}: %s{e.Message}. %s{e.StackTrace}"
            true))

    let configureServices (services: IServiceCollection) =
        services
            .AddOptions<JwtAuthenticationSettings>()
            .BindConfiguration(JwtAuthenticationSettings.JwtAuthentication)
            .ValidateDataAnnotations()
            .ValidateOnStart()
        |> ignore

        let serviceProvider = services.BuildServiceProvider()
        let jwtAuthenticationSettings =
            serviceProvider.GetService<IOptions<JwtAuthenticationSettings>>().Value
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(fun options ->
                options.TokenValidationParameters <-
                    TokenValidationParameters(
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = string jwtAuthenticationSettings.Issuer,
                        ValidAudience = string jwtAuthenticationSettings.Audience,
                        ClockSkew = TimeSpan.Zero,
                        IssuerSigningKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtAuthenticationSettings.SigningKey))
                    )

                // Leave in callbacks for troubleshooting JWT issues. Set a
                // breakpoint on lines below to track the JWT authentication
                // process.
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
            .AddMvc(fun options ->
                options.EnableEndpointRouting <- false
                options.Filters.Add(typeof<WebExceptionFilterAttribute>) |> ignore)
            .AddJsonOptions(fun options ->
                let o = options.JsonSerializerOptions
                // Per https://opensource.zalando.com/restful-api-guidelines/#118.
                o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
                // Per https://opensource.zalando.com/restful-api-guidelines/#169.
                o.Converters.Add(DateTimeJsonConverter())
                // Per https://opensource.zalando.com/restful-api-guidelines/#240.
                o.Converters.Add(EnumJsonConverter())
                o.WriteIndented <- true)
        |> ignore

        let configuration = serviceProvider.GetService<IConfiguration>()
        let connectionString = configuration.GetConnectionString("Scrum")
        services
            .AddHealthChecks()
            .AddTypeActivatedCheck<MemoryHealthCheck>("Memory", HealthStatus.Degraded, Seq.empty, args = [| int64 (5 * 1024) |])
            .AddTypeActivatedCheck<SQLiteHealthCheck>("Database", HealthStatus.Degraded, Seq.empty, args = [| connectionString |])
        |> ignore

        services.AddControllers() |> ignore
        services.AddResponseCaching() |> ignore
        services.AddEndpointsApiExplorer() |> ignore

        // Azure hosting under a Linux means the application is running a
        // container. Inside the container, the application is run using the
        // dotnet command, meaning Kestrel is serving traffic. Kestrel doesn't
        // have build-in compression support, so we add in application level
        // compression:
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

        services.AddGiraffe()

    let configureApplication (app: IApplicationBuilder) =
        //if app.Environment.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore else ()

        app.UseHttpsRedirection() |> ignore
        app.UseCors() |> ignore

        // Per https://opensource.zalando.com/restful-api-guidelines/#227 and
        // https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware
        app.UseResponseCaching() |> ignore
        app.Use(fun context (next: RequestDelegate) ->
            task {
                let r = context.Response
                r.GetTypedHeaders().CacheControl <-
                    CacheControlHeaderValue(
                        MustRevalidate = true,
                        MaxAge = TimeSpan.FromSeconds(seconds = 0),
                        NoCache = true,
                        NoStore = true
                    )
                r.Headers[HeaderNames.Vary] <- [| "Accept, Accept-Encoding" |] |> StringValues.op_Implicit
                return! next.Invoke(context)
            }
            :> Task)
        |> ignore

        let healthCheckOptions =
            let jsonOptions =
                JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true)
            jsonOptions.Converters.Add(ExceptionJsonConverter())
            HealthCheckOptions(
                ResponseWriter =
                    fun context report ->
                        task {
                            context.Response.ContentType <- "application/json; charset=utf-8"
                            let result =
                                JsonSerializer.Serialize(
                                    {| Status = string report.Status
                                       Result =
                                        report.Entries
                                        |> Seq.map (fun e ->
                                            {| Key = e.Key
                                               Value = string e.Value.Status
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
        app.UseGiraffe RouteHandler.webApp
        app

    [<EntryPoint>]
    let main args =
        let webApplicationBuilder = WebApplication.CreateBuilder(args)

        configureServices webApplicationBuilder.Services |> ignore
        let webApplication = webApplicationBuilder.Build()
        configureApplication webApplication |> ignore

        let logger = ScrumLogger.log (webApplication.Services.GetService<ILogger<_>>())
        let connectionString = webApplication.Configuration.GetConnectionString("Scrum")
        DatabaseMigration.Migrate(logger, connectionString).Apply()

        webApplication.Run()
        0
