namespace Scrum.Web

module Seedwork =
    open System
    open System.Text.Json
    open System.Text.Json.Serialization
    open Microsoft.AspNetCore.Http
    open Microsoft.AspNetCore.Mvc
    open Microsoft.Extensions.Primitives
    open Scrum.Shared.Application.Seedwork
    open Scrum.Shared.Infrastructure.Seedwork

    exception WebException of string

    let panic message : 't = raise (WebException(message))

    module ScrumRole =
        let fromString =
            function
            | "member" -> Member
            | "admin" -> Admin
            | unsupported -> panic $"Unsupported {nameof ScrumRole}: '{unsupported}'"

    module Json =
        // System.Text.Json cannot serialize an exception without itself
        // throwing an exception: "System.NotSupportedException: Serialization
        // and deserialization of 'System.Reflection.MethodBase' instances are
        // not supported. Path: $.Result.Exception.TargetSite.". This converter
        // works around the issue by limiting serialization to the most relevant
        // parts of the exception.
        type ExceptionJsonConverter() =
            inherit JsonConverter<Exception>()

            override _.Read(_, _, _) = unreachable "Never called"

            override x.Write(writer, value, options) =
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

    // RFC7807 problem details format per
    // https://opensource.zalando.com/restful-api-guidelines/#176.
    type ProblemDetails = { Type: string; Title: string; Status: int; Detail: string }

    module ProblemDetails =
        let create status detail : ProblemDetails = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

        let inferContentType (acceptHeaders: StringValues) =
            let ok =
                acceptHeaders.ToArray()
                |> Array.exists (fun v -> v = "application/problem+json")
            if ok then "application/problem+json" else "application/json"

        let toJsonResult acceptHeaders error : ActionResult =
            JsonResult(error, StatusCode = error.Status, ContentType = inferContentType acceptHeaders) :> _

        let createJsonResult acceptHeaders status detail =
            create status detail |> toJsonResult acceptHeaders

        let authorizationError (role: ScrumRole) =
            create StatusCodes.Status401Unauthorized $"Missing role: '{role.ToString()}'"

        type ValidationErrorResponse = { Field: string; Message: string }

        let errorMessageSerializationOptions =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

        let validationErrors (errors: ValidationError list) =
            (errors |> List.map (fun e -> { Field = e.Field; Message = e.Message }), errorMessageSerializationOptions)
            |> JsonSerializer.Serialize
            |> create StatusCodes.Status400BadRequest

        let missingQueryStringParameter name =
            create StatusCodes.Status400BadRequest $"Missing query string parameter '%s{name}'"

        let unexpectedQueryStringParameters names =
            let names = String.Join(", ", names |> List.map (fun s -> $"'%s{s}'"))
            create StatusCodes.Status400BadRequest $"Unexpected query string parameters: %s{names}"

        let queryStringParameterMustBeOfType name type_ =
            create StatusCodes.Status400BadRequest $"Query string parameter '%s{name}' must be an %s{type_}"

module Configuration =
    open System
    open System.ComponentModel.DataAnnotations

    type JwtAuthenticationSettings() =
        static member JwtAuthentication: string = nameof JwtAuthenticationSettings.JwtAuthentication
        [<Required>]
        member val Issuer: Uri = null with get, set
        [<Required>]
        member val Audience: Uri = null with get, set
        [<Required>]
        member val SigningKey: string = null with get, set
        [<Range(60, 86400)>]
        member val ExpirationInSeconds: uint = 0ul with get, set

module Service =
    open System
    open System.IdentityModel.Tokens.Jwt
    open System.Security.Claims
    open System.Text
    open Microsoft.AspNetCore.Http
    open Microsoft.IdentityModel.JsonWebTokens
    open Microsoft.IdentityModel.Tokens
    open Scrum.Shared.Application.Seedwork
    open Seedwork
    open Configuration

    // Names of claims shared between services.
    module ScrumClaims =
        let UserIdClaim = "userId"
        let RolesClaim = "roles"

    // Web specific implementation of IUserIdentity. It therefore belongs in
    // Program.fs rather than Infrastructure.fs.
    module UserIdentity =
        let getCurrentIdentity(context: HttpContext) =
            if isNull context then
                Anonymous
            else
                let claimsPrincipal = context.User
                if isNull claimsPrincipal then
                    Anonymous
                else
                    let claimsIdentity = claimsPrincipal.Identity :?> ClaimsIdentity
                    let claims = claimsIdentity.Claims
                    if Seq.isEmpty claims then
                        Anonymous
                    else
                        let userIdClaim =
                            claims
                            |> Seq.filter (fun c -> c.Type = ScrumClaims.UserIdClaim)
                            |> Seq.map _.Value
                            |> Seq.exactlyOne
                        let rolesClaim =
                            claims
                            |> Seq.filter (fun c -> c.Type = ClaimTypes.Role)
                            |> Seq.map (fun c -> ScrumRole.fromString c.Value)
                            |> List.ofSeq

                        // With a proper identity provider, we'd likely have
                        // kinds of authenticated identities, and we might
                        // use a claim's value to determine which one.
                        match List.length rolesClaim with
                        | 0 -> Anonymous
                        | _ -> Authenticated(userIdClaim, rolesClaim)

    module IdentityProvider =
        let sign (settings: JwtAuthenticationSettings) (now: DateTime) claims =
            let securityKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey))
            let credentials = SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
            let validUntilUtc = now.AddSeconds(int settings.ExpirationInSeconds)
            let token =
                JwtSecurityToken(
                    string settings.Issuer,
                    string settings.Audience,
                    claims,
                    expires = validUntilUtc,
                    signingCredentials = credentials
                )
            JwtSecurityTokenHandler().WriteToken(token)

        let issueToken (settings: JwtAuthenticationSettings) (now: DateTime) userId roles =
            // With an actual user store, we'd validate user credentials here.
            // But for this application, userId may be any string and role must
            // be either "member" or "admin".
            let roles =
                roles
                |> List.map (fun r -> Claim(ScrumClaims.RolesClaim, string r))
                |> List.toArray
            let rest =
                [| Claim(JwtRegisteredClaimNames.Jti, string (Guid.NewGuid()))
                   Claim(ScrumClaims.UserIdClaim, userId) |]
            Array.concat [ roles; rest ] |> sign settings now

        let renewToken settings now identity =
            match identity with
            | Anonymous -> Error "User is anonymous"
            | Authenticated(id, roles) -> Ok(issueToken settings now id roles)

open Service

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
    open Seedwork

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

module RouteHandlers =
    open System
    open System.Security.Claims
    open System.Collections.Generic
    open System.Data.SQLite
    open System.Text.Json
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.Options
    open Microsoft.Extensions.Logging
    open Microsoft.Extensions.Configuration
    open Giraffe
    open FsToolkit.ErrorHandling
    open Seedwork
    open Scrum.Shared.Application.Seedwork
    open Scrum.Story.Application.StoryRequest
    open Scrum.Shared.Application.DomainEventRequest
    open Scrum.Shared.Infrastructure.Seedwork
    open Configuration

    let errorMessageSerializationOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

    let verifyOnlyExpectedQueryStringParameters (query: IQueryCollection) expectedParameters =
        // Per design APIs conservatively:
        // https://opensource.zalando.com/restful-api-guidelines/#109
        let unexpected =
            query
            |> Seq.map _.Key
            |> Seq.toList
            |> List.except expectedParameters
        if List.isEmpty unexpected then
            Ok ()
        else
            Error (ProblemDetails.unexpectedQueryStringParameters unexpected)

    let verifyUserIsAuthenticated : HttpHandler =
        // TODO: this function comes part of Giraffe.
        fun (next : HttpFunc) (ctx : HttpContext) ->
            if isNotNull ctx.User && ctx.User.Identity.IsAuthenticated
            then next ctx
            else setStatusCode 401 earlyReturn ctx

    let issueTokenHandler : HttpHandler =
        // TODO: Fail if token is provided and/or user isn't anonymous
        fun (next : HttpFunc) (ctx : HttpContext) ->
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
                ctx.SetHttpHeader("location", "/authentication/introspect")
                json r next ctx
            | Error e ->
                ctx.SetStatusCode 400
                ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                json e next ctx

    let renewTokenHandler : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            let settings = ctx.GetService<IOptions<JwtAuthenticationSettings>>()
            let settings = settings.Value
            let identity = UserIdentity.getCurrentIdentity ctx
            let result = IdentityProvider.renewToken settings DateTime.UtcNow identity
            match result with
            | Ok token ->
                ctx.SetStatusCode 201
                ctx.SetHttpHeader("location", "/authentication/introspect")
                json {| Token = token |} next ctx
            | Error e ->
                ctx.SetStatusCode 400
                ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                json e next ctx

    let introspectTokenHandler : HttpHandler =
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

    let utcNow () = DateTime.UtcNow

    let getConnection (connectionString: string): SQLiteConnection =
        let connection = new SQLiteConnection(connectionString)
        connection.Open()
        use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
        cmd.ExecuteNonQuery() |> ignore
        connection

    module CaptureBasicStoryDetails =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open CaptureBasicStoryDetailsCommand
        
        type Request = { title: string; description: string }

        let handler : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                // TODO: verify no query string args passed
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")

                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let storyExist = SqliteStoryRepository.existAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: CaptureBasicStoryDetailsCommand =
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow storyExist storyApplyEvent identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{id}") // TODO: should headers be capitalized?
                        return! json {| StoryId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | DuplicateStory id -> unreachable (string id)
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module ReviseBasicStoryDetails =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open ReviseBasicStoryDetailsCommand
        type Request = { title: string; description: string }

        let handler storyId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                // TODO: verify no query string args passed
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicStoryDetailsCommand =
                        { Id = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{id}")
                        return! json {| StoryId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module AddBasicTaskDetailsToStory =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open AddBasicTaskDetailsToStoryCommand
        
        type Request = { title: string; description: string }

        let handler storyId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: AddBasicTaskDetailsToStoryCommand =
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{storyId}/tasks/{taskId}")
                        return! json {| TaskId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | DuplicateTask id -> unreachable (string id)
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module ReviseBasicTaskDetails =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open ReviseBasicTaskDetailsCommand
        
        type Request = { title: string; description: string }
        
        let handler (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicTaskDetailsCommand =
                        { StoryId = storyId
                          TaskId = taskId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{storyId}/tasks/{taskId}")
                        return! json {| TaskId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module RemoveTask =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open RemoveTaskCommand
    
        let handler (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let cmd: RemoveTaskCommand = { StoryId = storyId; TaskId = taskId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module RemoveStory =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open RemoveStoryCommand
    
        let handler storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let cmd: RemoveStoryCommand = { Id = storyId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound _ -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }
                
    module GetStoryById =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open GetStoryByIdQuery

        let handler storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted

                    let qry: GetStoryByIdQuery = { Id = storyId }
                    let! result =
                        runWithMiddlewareAsync log identity qry
                            (fun () -> runAsync getStoryById identity qry)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    let toPagedResult result (ctx: HttpContext) (next: HttpFunc) =
        task {
            match result with
            | Ok paged ->
                ctx.SetStatusCode 200
                return! json paged next ctx
            | Error e ->
                ctx.SetStatusCode e.Status
                ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                return! json e next ctx
        }

    let stringToInt32 field (value: string) =
        let ok, value = Int32.TryParse(value)
        if ok then
           Ok value
        else
           Error (ProblemDetails.queryStringParameterMustBeOfType field "integer")

    module GetStoriesPaged =
        open Scrum.Shared.Infrastructure
        open Scrum.Story.Infrastructure
        open GetStoriesPagedQuery
        
        let handler : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    let! result =
                        taskResult {
                            let! limit =
                                ctx.GetQueryStringValue "limit"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "limit")
                                |> Result.bind (stringToInt32 "limit")
                            let! cursor =
                                ctx.GetQueryStringValue "cursor"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "cursor")
                            let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof limit; nameof cursor ]

                            use connection = getConnection connectionString
                            use transaction = connection.BeginTransaction()
                            let getStoriesPaged = SqliteStoryRepository.getPagedAsync transaction ctx.RequestAborted

                            let qry: GetStoriesPagedQuery = { Limit = limit; Cursor = cursor |> Option.ofObj }
                            let! result =
                                runWithMiddlewareAsync log identity qry
                                    (fun () -> runAsync getStoriesPaged identity qry)
                                |> TaskResult.mapError(
                                        function
                                        | AuthorizationError role -> ProblemDetails.authorizationError role
                                        | ValidationErrors ve -> ProblemDetails.validationErrors ve)
                            do! transaction.RollbackAsync(ctx.RequestAborted)
                            return result
                        }

                    return! toPagedResult result ctx next
                }

    module GetPersistedDomainEvents =
        open Scrum.Shared.Infrastructure
        open GetByAggregateIdQuery
    
        let handler aggregateId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    let! result =
                        taskResult {
                            let! limit =
                                ctx.GetQueryStringValue "limit"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "limit")
                                |> Result.bind (stringToInt32 "limit")
                            let! cursor =
                                ctx.GetQueryStringValue "cursor"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "cursor")
                            let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof limit; nameof cursor ]

                            use connection = getConnection connectionString
                            use transaction = connection.BeginTransaction()
                            let getByAggregateId = SqliteDomainEventRepository.getByAggregateIdAsync transaction ctx.RequestAborted

                            let qry: GetByAggregateIdQuery = { Id = aggregateId; Limit = limit; Cursor = cursor |> Option.ofObj }
                            let! result =
                                runWithMiddlewareAsync log identity qry
                                    (fun () -> runAsync getByAggregateId identity qry)
                                |> TaskResult.mapError(
                                    function
                                    | AuthorizationError role -> ProblemDetails.authorizationError role
                                    | ValidationErrors ve -> ProblemDetails.validationErrors ve)
                            do! transaction.RollbackAsync(ctx.RequestAborted)
                            return result
                        }

                    return! toPagedResult result ctx next
                }

    let introspectTestHandler : HttpHandler=
        fun (next : HttpFunc) (ctx : HttpContext) ->
            // API gateways and other proxies between the client and the service
            // tag on extra information to the request. This endpoint allows a
            // client to see what the request looked like from the server's
            // point of view.
            let headers = ctx.Request.Headers |> Seq.map (fun h -> KeyValuePair(h.Key, string h.Value))
            ctx.SetStatusCode 200
            json headers next ctx

    let currentTimeTestHandler : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            // Useful for establishing baseline performance numbers and testing
            // rate limits. Because this action doesn't perform significant
            // work, it provides an upper bound for requests/second given a
            // response time distribution.
            text (string DateTime.UtcNow) next ctx

    let webApp =
        choose [
            // Loosely modeled after the corresponding OAuth2 endpoints.
            POST >=> choose [
                route "/authentication/issue-token" >=> issueTokenHandler
                route "/authentication/renew-token" >=> verifyUserIsAuthenticated >=> renewTokenHandler
                route "/authentication/introspect" >=> verifyUserIsAuthenticated >=> introspectTokenHandler ]

            verifyUserIsAuthenticated >=> choose [
                POST >=> route "/stories" >=> CaptureBasicStoryDetails.handler
                PUT >=> routef "/stories/%O" ReviseBasicStoryDetails.handler
                POST >=> routef "/stories/%O/tasks" AddBasicTaskDetailsToStory.handler
                PUT >=> routef "/stories/%O/tasks/%O" ReviseBasicTaskDetails.handler
                DELETE >=> routef "/stories/%O/tasks/%O" RemoveTask.handler
                DELETE >=> routef "/stories/%O" RemoveStory.handler
                GET >=> routef "/stories/%O" GetStoryById.handler
                GET >=> route "/stories" >=> GetStoriesPaged.handler
            ]

            verifyUserIsAuthenticated >=> choose [
                GET >=> routef "/persisted-domain-events/%O" GetPersistedDomainEvents.handler
            ]

            GET >=> choose [
                route "/tests/introspect" >=> introspectTestHandler
                route "/tests/current-time" >=> currentTimeTestHandler
            ]

            RequestErrors.NOT_FOUND "Not Found"
        ]

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
    open Scrum.Shared.Infrastructure.Seedwork.Json
    open Seedwork
    open HealthCheck
    open Filter
    open Configuration

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
            .AddTypeActivatedCheck<SQLiteHealthCheck>(
                "Database",
                HealthStatus.Degraded,
                Seq.empty,
                args = [| connectionString |]
            )
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
                    CacheControlHeaderValue(MustRevalidate = true, MaxAge = TimeSpan.FromSeconds(seconds = 0), NoCache = true, NoStore = true)
                r.Headers[HeaderNames.Vary] <- [| "Accept, Accept-Encoding" |] |> StringValues.op_Implicit
                return! next.Invoke(context)
            }
            :> Task)
        |> ignore

        let healthCheckOptions =
            let jsonOptions =
                JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true)
            jsonOptions.Converters.Add(Json.ExceptionJsonConverter())
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
        app.UseGiraffe RouteHandlers.webApp
        app

    open Scrum.Shared.Infrastructure
    
    [<EntryPoint>]
    let main args =
        let webApplicationBuilder = WebApplication.CreateBuilder(args)

        configureServices(webApplicationBuilder.Services) |> ignore
        let webApplication = webApplicationBuilder.Build()
        configureApplication webApplication |> ignore

        let logger = ScrumLogger.log (webApplication.Services.GetService<ILogger<_>>())
        let connectionString = webApplication.Configuration.GetConnectionString("Scrum")
        DatabaseMigration.Migrate(logger, connectionString).Apply()

        webApplication.Run()
        0
