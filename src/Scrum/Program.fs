namespace Scrum.Web

module Seedwork =
    open System
    open System.Text.Json
    open System.Text.Json.Serialization
    open Microsoft.AspNetCore.Http
    open Microsoft.AspNetCore.Mvc
    open Microsoft.Extensions.Primitives
    open Scrum.Application.Seedwork
    open Scrum.Infrastructure.Seedwork
    
    exception WebException of string

    let panic (s: string) : 't = raise (WebException(s))

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

    // RFC7807 problem details format per
    // https://opensource.zalando.com/restful-api-guidelines/#176.
    type ProblemDetails = { Type: string; Title: string; Status: int; Detail: string }

    module ProblemDetails =
        let create status detail : ProblemDetails = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

        let inferContentType (acceptHeaders: StringValues) : string =
            let ok =
                acceptHeaders.ToArray()
                |> Array.exists (fun v -> v = "application/problem+json")
            if ok then "application/problem+json" else "application/json"  
        
        let toJsonResult (accept: StringValues) (error: ProblemDetails) : ActionResult =
            JsonResult(error, StatusCode = error.Status, ContentType = inferContentType accept) :> _

        let createJsonResult (accept: StringValues) (status: int) (detail: string) : ActionResult =
            create status detail |> toJsonResult accept

        let fromAuthorizationError (accept: StringValues) (message: string) : ActionResult =
            createJsonResult accept StatusCodes.Status401Unauthorized message

        let fromUnexpectedQueryStringParameters (accept: StringValues) (unexpected: string list) : ActionResult =
            let parameters = String.Join(", ", unexpected |> List.toSeq)
            createJsonResult accept StatusCodes.Status400BadRequest $"Unexpected query string parameters: {parameters}"

        type ValidationErrorDto = { Field: string; Message: string }

        let errorMessageSerializationOptions =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

        let fromValidationErrors (accept: StringValues) (errors: ValidationError list) : ActionResult =
            (errors |> List.map (fun e -> { Field = e.Field; Message = e.Message }), errorMessageSerializationOptions)
            |> JsonSerializer.Serialize
            |> createJsonResult accept StatusCodes.Status400BadRequest

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

open Configuration

module Service =
    open System
    open System.Data.SQLite
    open System.IO
    open System.IdentityModel.Tokens.Jwt
    open System.Reflection
    open System.Security.Claims
    open System.Security.Cryptography
    open System.Text
    open Microsoft.AspNetCore.Http
    open Microsoft.IdentityModel.JsonWebTokens
    open Microsoft.IdentityModel.Tokens
    open Scrum.Application.Seedwork
    open Scrum.Infrastructure.Seedwork
    open Seedwork    
    
    // Names of claims shared between services.
    module ScrumClaims =
        let UserIdClaim = "userId"
        let RolesClaim = "roles"

    // Web specific implementation of IUserIdentity. It therefore belongs in
    // Program.fs rather than Infrastructure.fs.
    module UserIdentity =
        let getCurrentIdentity(context: HttpContext) : ScrumIdentity =
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
        let sign (settings: JwtAuthenticationSettings) (now: DateTime) (claims: Claim array) : string =
            let securityKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey))
            let credentials = SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
            let validUntilUtc = now.AddSeconds(int settings.ExpirationInSeconds)
            let token =
                JwtSecurityToken(
                    settings.Issuer.ToString(),
                    settings.Audience.ToString(),
                    claims,
                    expires = validUntilUtc,
                    signingCredentials = credentials
                )
            JwtSecurityTokenHandler().WriteToken(token)        
        
        let issueToken (settings: JwtAuthenticationSettings) (now: DateTime) (userId: string) (roles: ScrumRole list) : string =
            // With an actual user store, we'd validate user credentials here.
            // But for this application, userId may be any string and role must
            // be either "member" or "admin".
            let roles =
                roles
                |> List.map (fun r -> Claim(ScrumClaims.RolesClaim, r.ToString()))
                |> List.toArray
            let rest =
                [| Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
                   Claim(ScrumClaims.UserIdClaim, userId) |]
            Array.concat [ roles; rest ] |> sign settings now

        let renewToken (settings: JwtAuthenticationSettings) (now: DateTime) (identity: ScrumIdentity) : Result<string, string> =
            match identity with
            | Anonymous -> Error "User is anonymous"
            | Authenticated(id, roles) -> Ok(issueToken settings now id roles)                    

    type AvailableScript = { Name: string; Hash: string; Sql: string }
    type AppliedMigration = { Name: string; Hash: string; Sql: string; CreatedAt: DateTime }

    type DatabaseMigrator(log: LogMessage -> unit, connectionString: string) =
        let createMigrationsSql =
            "
            create table migrations(
                name text primary key,
                hash text not null,
                sql text not null,
                created_at integer not null
            ) strict;"

        let getAvailableScripts () : AvailableScript array =
            let hasher = SHA1.Create()
            let assembly = Assembly.GetExecutingAssembly()
            let prefix = "Scrum.Sql."

            assembly.GetManifestResourceNames()
            |> Array.filter _.StartsWith(prefix)
            |> Array.map (fun path ->
                let sql =
                    use stream = assembly.GetManifestResourceStream(path)
                    if isNull stream then
                        // On the SQL file, did you set Build action to
                        // EmbeddedResource?
                        panic $"Embedded resource not found: '{path}'"
                    use reader = new StreamReader(stream)
                    reader.ReadToEnd()

                let hash =
                    hasher.ComputeHash(Encoding.UTF8.GetBytes(sql))
                    |> Array.map _.ToString("x2")
                    |> String.Concat

                let name = path.Replace(prefix, "").Replace(".sql", "")
                { Name = name; Hash = hash; Sql = sql })
            |> Array.sortBy _.Name

        let getAppliedMigrations (connection: SQLiteConnection) : AppliedMigration array =
            let sql =
                "select count(*) from sqlite_master where type = 'table' and name = 'migrations'"
            use cmd = new SQLiteCommand(sql, connection)
            let exist = cmd.ExecuteScalar() :?> int64

            if exist = 0 then
                // SQLite doesn't support transactional schema changes.
                log (Information2 "reating migrations table")
                use cmd = new SQLiteCommand(createMigrationsSql, connection)
                let count = cmd.ExecuteNonQuery()
                assert (count = 0)
                Array.empty
            else
                let sql = "select name, hash, sql, created_at from migrations order by created_at"
                use cmd = new SQLiteCommand(sql, connection)

                // Without transaction as we assume only one migration will run
                // at once.
                use r = cmd.ExecuteReader()

                let migrations = ResizeArray<AppliedMigration>()
                while r.Read() do
                    let m =
                        { Name = r["name"] |> string
                          Hash = r["hash"] |> string
                          Sql = r["sql"] |> string
                          CreatedAt = Repository.parseCreatedAt r["created_at"] }
                    migrations.Add(m)
                migrations |> Seq.toArray

        let verifyAppliedMigrations (available: AvailableScript array) (applied: AppliedMigration array) : unit =
            for i = 0 to applied.Length - 1 do
                if applied[i].Name <> available[i].Name then
                    panic $"Mismatch in applied name '{applied[i].Name}' and available name '{available[i].Name}'"
                if applied[i].Hash <> available[i].Hash then
                    panic $"Mismatch in applied hash '{applied[i].Hash}' and available hash '{available[i].Hash}'"

        let applyNewMigrations (connection: SQLiteConnection) (available: AvailableScript array) (applied: AppliedMigration array) : unit =
            for i = applied.Length to available.Length - 1 do
                // With a transaction as we're updating the migrations table.
                use tx = connection.BeginTransaction()
                use cmd = new SQLiteCommand(available[i].Sql, connection, tx)
                let count = cmd.ExecuteNonQuery()
                assert (count >= 0)

                let sql =
                    $"insert into migrations ('name', 'hash', 'sql', 'created_at') values ('{available[i].Name}', '{available[i].Hash}', '{available[i].Sql}', {DateTime.UtcNow.Ticks})"
                let cmd = new SQLiteCommand(sql, connection, tx)

                try
                    log (Information2 $"Applying migration: '{available[i].Name}'")
                    let count = cmd.ExecuteNonQuery()
                    assert (count = 1)

                    // Schema upgrade per migration code. Downgrading isn't
                    // supported.
                    match available[i].Name with
                    | "202310051903-initial" -> ()
                    | _ -> ()

                    tx.Commit()
                with e ->
                    tx.Rollback()
                    reraise ()

        let applySeed (connection: SQLiteConnection) (seed: AvailableScript) =
            // A pseudo-migration, so we don't record it in the migrations
            // table.
            use tx = connection.BeginTransaction()
            use cmd = new SQLiteCommand(seed.Sql, connection, tx)
            try
                log (Information2 "Applying seed")
                let count = cmd.ExecuteNonQuery()
                assert (count >= -1)
                tx.Commit()
            with e ->
                tx.Rollback()
                reraise ()

        member _.Apply() : unit =
            use connection = new SQLiteConnection(connectionString)
            connection.Open()

            let availableScripts = getAvailableScripts ()
            let availableMigrations =
                availableScripts |> Array.filter (fun m -> m.Name <> "seed")

            log (Information2 $"Found {availableScripts.Length} available migration(s)")
            let appliedMigrations = getAppliedMigrations connection
            log (Information2 $"Found {appliedMigrations.Length} applied migration(s)")

            verifyAppliedMigrations availableMigrations appliedMigrations
            applyNewMigrations connection availableMigrations appliedMigrations

            let seeds = availableScripts |> Array.filter (fun s -> s.Name = "seed")
            log (Information2 $"Found {seeds.Length} seed")
            seeds |> Array.exactlyOne |> applySeed connection

open Service

module HealthCheck =
    open System
    open System.Collections.Generic
    open System.Data.SQLite
    open System.Threading.Tasks
    open Microsoft.Extensions.Diagnostics.HealthChecks
    open Scrum.Application.Seedwork
    
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


module Filter =
    open System.Diagnostics
    open System.Net
    open Microsoft.AspNetCore.Mvc.Filters
    open Microsoft.Extensions.Hosting
    open Microsoft.AspNetCore.Mvc
    open Scrum.Infrastructure.Seedwork
    open Seedwork   
    
    type WebExceptionFilterAttribute(hostEnvironment: IHostEnvironment) =
        inherit ExceptionFilterAttribute()

        override _.OnException(context: ExceptionContext) : unit =
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

module Routes (* Handlers *) =
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
    open Scrum.Application.Seedwork
    open Scrum.Application.StoryAggregateRequest
    open Scrum.Application.DomainEventRequest
    open Scrum.Infrastructure
    open Scrum.Infrastructure.Seedwork
    
    let errorMessageSerializationOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)    
    
    let fromValidationErrors (errors: ValidationError list): ProblemDetails =
        (errors |> List.map (fun e -> { Field = e.Field; Message = e.Message }), errorMessageSerializationOptions)
        |> JsonSerializer.Serialize
        |> ProblemDetails.create 400        
    
    let fromAuthorizationError (message: string): ProblemDetails =
        ProblemDetails.create 401 message
    
    let verifyOnlyExpectedQueryStringParameters (query: IQueryCollection) (expectedParameters: string list): Result<unit, ProblemDetails> =
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
            let unexpected = String.Join(", ", unexpected |> List.toSeq)
            Error (ProblemDetails.create 400 $"Unexpected query string parameters: %s{unexpected}")
   
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
                        |> Result.mapError (fun e -> ProblemDetails.create 400 "Missing query string parameter 'userId'")
                    let! roles =
                        ctx.GetQueryStringValue "roles"
                        |> Result.map (fun r -> r.Split(',') |> Array.map ScrumRole.fromString |> Array.toList)
                        |> Result.mapError (fun e -> ProblemDetails.create 400 "Missing query string parameter 'roles'")
                    let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof userId; nameof roles ] 
                    let token = IdentityProvider.issueToken settings DateTime.UtcNow userId roles
                    
                    // As the token is opaque, we can either promote information from inside the
                    // token to fields on the response object or provide clients with an introspect
                    // endpoint. We chose the latter while still wrapping the token in a response.
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
                // Special case non-string value or it becomes a string in
                // string in the JSON rendering of the claim.
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
           
    let currentUtc () = DateTime.UtcNow
           
    let getConnection (connectionString: string): SQLiteConnection =
        let connection = new SQLiteConnection(connectionString)
        connection.Open()
        use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
        cmd.ExecuteNonQuery() |> ignore
        connection        

    type StoryCreateDto = { title: string; description: string }
               
    let captureBasicStoryDetailsHandler : HttpHandler =
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

                let! request = ctx.BindJsonAsync<StoryCreateDto>()
                let! result =
                    CaptureBasicStoryDetailsCommand.runAsync
                        log
                        currentUtc
                        storyExist
                        storyApplyEvent
                        identity
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                        
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
                        | CaptureBasicStoryDetailsCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | CaptureBasicStoryDetailsCommand.ValidationErrors ve -> fromValidationErrors ve
                        | CaptureBasicStoryDetailsCommand.DuplicateStory id -> unreachable (string id)
                        | CaptureBasicStoryDetailsCommand.DuplicateTasks ids -> unreachable (String.Join(", ", ids))
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }

    type StoryUpdateDto = { title: string; description: string }

    let reviseBasicStoryDetailsHandler (storyId: Guid) : HttpHandler =
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
                
                let! request = ctx.BindJsonAsync<StoryUpdateDto>()
                let! result =
                    ReviseBasicStoryDetailsCommand.runAsync
                        log
                        currentUtc
                        getStoryById
                        storyApplyEvent
                        identity
                        { Id = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                        
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
                        | ReviseBasicStoryDetailsCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | ReviseBasicStoryDetailsCommand.ValidationErrors ve -> fromValidationErrors ve
                        | ReviseBasicStoryDetailsCommand.StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }
    
    type AddTaskToStoryDto = { title: string; description: string }    
    
    let addBasicTaskDetailsToStoryHandler (storyId: Guid) : HttpHandler =
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
                
                let! request = ctx.BindJsonAsync<AddTaskToStoryDto>()
                let! result =
                    AddBasicTaskDetailsToStoryCommand.runAsync
                        log
                        currentUtc
                        getStoryById
                        storyApplyEvent
                        identity
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                        
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
                        | AddBasicTaskDetailsToStoryCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | AddBasicTaskDetailsToStoryCommand.ValidationErrors ve -> fromValidationErrors ve
                        | AddBasicTaskDetailsToStoryCommand.StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        | AddBasicTaskDetailsToStoryCommand.DuplicateTask id -> unreachable (string id)
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }
            
    let reviseBasicTaskDetailsHandler (storyId: Guid, taskId: Guid) : HttpHandler =
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
                
                let! request = ctx.BindJsonAsync<AddTaskToStoryDto>()
                let! result =
                    ReviseBasicTaskDetailsCommand.runAsync
                        log
                        currentUtc
                        getStoryById
                        storyApplyEvent
                        identity
                        { StoryId = storyId
                          TaskId = taskId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                        
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
                        | ReviseBasicTaskDetailsCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | ReviseBasicTaskDetailsCommand.ValidationErrors ve -> fromValidationErrors ve
                        | ReviseBasicTaskDetailsCommand.StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        | ReviseBasicTaskDetailsCommand.TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }
            
    let removeTaskHandler (storyId: Guid, taskId: Guid) : HttpHandler =
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
                
                let! result =
                    RemoveTaskCommand.runAsync 
                        log
                        currentUtc
                        getStoryById
                        storyApplyEvent
                        identity
                        { StoryId = storyId; TaskId = taskId }

                match result with
                | Ok _ ->
                    do! transaction.CommitAsync(ctx.RequestAborted)
                    ctx.SetStatusCode 200
                    return! json {||} next ctx
                | Error e ->
                    do! transaction.RollbackAsync(ctx.RequestAborted)
                    let problem =
                        match e with
                        | RemoveTaskCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | RemoveTaskCommand.ValidationErrors ve -> fromValidationErrors ve
                        | RemoveTaskCommand.StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        | RemoveTaskCommand.TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }
    
    let removeStoryHandler (storyId: Guid) : HttpHandler =
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
                
                let! result =
                    RemoveStoryCommand.runAsync 
                        log
                        currentUtc
                        getStoryById
                        storyApplyEvent
                        identity
                        { Id = storyId }

                match result with
                | Ok _ ->
                    do! transaction.CommitAsync(ctx.RequestAborted)
                    ctx.SetStatusCode 200
                    return! json {||} next ctx
                | Error e ->
                    do! transaction.RollbackAsync(ctx.RequestAborted)
                    let problem =
                        match e with
                        | RemoveStoryCommand.AuthorizationError ae -> fromAuthorizationError ae
                        | RemoveStoryCommand.ValidationErrors ve -> fromValidationErrors ve
                        | RemoveStoryCommand.StoryNotFound _ -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }
            
    let getStoryByIdHandler (storyId: Guid) : HttpHandler =
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
                
                let! result =
                    GetStoryByIdQuery.runAsync 
                        log
                        getStoryById
                        identity
                        { Id = storyId }

                match result with
                | Ok _ ->
                    do! transaction.CommitAsync(ctx.RequestAborted)
                    ctx.SetStatusCode 200
                    return! json {||} next ctx
                | Error e ->
                    do! transaction.RollbackAsync(ctx.RequestAborted)
                    let problem =
                        match e with
                        | GetStoryByIdQuery.AuthorizationError ae -> fromAuthorizationError ae
                        | GetStoryByIdQuery.ValidationErrors ve -> fromValidationErrors ve
                        | GetStoryByIdQuery.StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                    ctx.SetStatusCode problem.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json problem next ctx
            }                
    
    let getStoriesPagedHandler : HttpHandler =
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
                            // TODO: generalize message generation.
                            |> Result.mapError (fun _ -> ProblemDetails.create 400 "Missing query string parameter 'limit'")
                            |> Result.bind (fun limit ->
                                   // TODO: Extract into helper function taking in string field name.
                                   let ok, limit = Int32.TryParse(limit)
                                   if ok then
                                       Ok limit
                                    else
                                       Error (ProblemDetails.create 400 "Query string parameter 'limit' must be an integer"))
                        let! cursor =
                            ctx.GetQueryStringValue "cursor"
                            |> Result.mapError (fun _ -> ProblemDetails.create 400 "Missing query string parameter 'cursor'")
                        let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof limit; nameof cursor ]                                        
                                                             
                        use connection = getConnection connectionString
                        use transaction = connection.BeginTransaction()
                        let getStoriesPaged = SqliteStoryRepository.getStoriesPagedAsync transaction ctx.RequestAborted
                        
                        let! result =
                            GetStoriesPagedQuery.runAsync 
                                log
                                getStoriesPaged
                                identity
                                { Limit = limit; Cursor = cursor |> Option.ofObj }
                            |> TaskResult.mapError(fun e ->
                                let problem =
                                    match e with
                                    | GetStoriesPagedQuery.AuthorizationError ae -> fromAuthorizationError ae
                                    | GetStoriesPagedQuery.ValidationErrors ve -> fromValidationErrors ve
                                problem)
                        do! transaction.RollbackAsync(ctx.RequestAborted)                            
                        return result
                    }
                
                // TODO: Extract into helper function.
                match result with
                | Ok paged ->
                    ctx.SetStatusCode 200
                    return! json paged next ctx
                | Error e ->
                    ctx.SetStatusCode e.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json e next ctx
            }      
     
    let getPersistedDomainEvents (aggregateId: Guid) : HttpHandler =
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
                            // TODO: generalize message generation.
                            |> Result.mapError (fun _ -> ProblemDetails.create 400 "Missing query string parameter 'limit'")
                            |> Result.bind (fun limit ->
                                   // TODO: Extract into helper function taking in string field name.
                                   let ok, limit = Int32.TryParse(limit)
                                   if ok then
                                       Ok limit
                                    else
                                       Error (ProblemDetails.create 400 "Query string parameter 'limit' must be an integer"))
                        let! cursor =
                            ctx.GetQueryStringValue "cursor"
                            |> Result.mapError (fun _ -> ProblemDetails.create 400 "Missing query string parameter 'cursor'")
                        let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof limit; nameof cursor ]                                        

                        use connection = getConnection connectionString
                        use transaction = connection.BeginTransaction()
                        let getByAggregateId = SqliteDomainEventRepository.getByAggregateIdAsync transaction ctx.RequestAborted

                        let! result =
                            GetByAggregateIdQuery.runAsync 
                                log
                                getByAggregateId
                                identity
                                { Id = aggregateId; Limit = limit; Cursor = cursor |> Option.ofObj }
                            |> TaskResult.mapError(fun e ->
                                let problem =
                                    match e with
                                    | GetByAggregateIdQuery.AuthorizationError ae -> fromAuthorizationError ae
                                    | GetByAggregateIdQuery.ValidationErrors ve -> fromValidationErrors ve
                                problem)
                        do! transaction.RollbackAsync(ctx.RequestAborted)                            
                        return result                   
                    }
                    
                // TODO: Extract into helper function.
                match result with
                | Ok paged ->
                    ctx.SetStatusCode 200
                    return! json paged next ctx
                | Error e ->
                    ctx.SetStatusCode e.Status                        
                    ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                    return! json e next ctx                
            }     
     
    let introspectTestHandler : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            // API gateways and other proxies between the client and the
            // service tag on extra information to the request. This endpoint
            // allows a client to see what the request looked like from the
            // server's point of view.
            let headers = ctx.Request.Headers |> Seq.map (fun h -> KeyValuePair(h.Key, h.Value.ToString()))
            ctx.SetStatusCode 200
            json headers next ctx     

    let currentTimeTestHandler : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            // Useful for establishing baseline performance numbers and testing
            // rate limits. Because this action doesn't perform significant
            // work, it provides an upper bound for requests/second given a
            // response time distribution.
            text (DateTime.UtcNow.ToString()) next ctx       
     
    let webApp =
        choose [
            // Loosely modeled after the corresponding OAuth2 endpoints.           
            POST >=> choose [
                route "/authentication/issue-token" >=> issueTokenHandler
                route "/authentication/renew-token" >=> verifyUserIsAuthenticated >=> renewTokenHandler
                route "/authentication/introspect" >=> verifyUserIsAuthenticated >=> introspectTokenHandler ]
            
            verifyUserIsAuthenticated >=> choose [
                POST >=> route "/stories" >=> captureBasicStoryDetailsHandler
                PUT >=> routef "/stories/%O" reviseBasicStoryDetailsHandler
                POST >=> routef "/stories/%O/tasks" addBasicTaskDetailsToStoryHandler
                PUT >=> routef "/stories/%O/tasks/%O" reviseBasicTaskDetailsHandler
                DELETE >=> routef "/stories/%O/tasks/%O" removeTaskHandler
                DELETE >=> routef "/stories/%O" removeStoryHandler
                GET >=> routef "/stories/%O" getStoryByIdHandler
                GET >=> route "/stories" >=> getStoriesPagedHandler                                       
            ]
            
            verifyUserIsAuthenticated >=> choose [
                GET >=> routef "/persisted-domain-events/%O" getPersistedDomainEvents
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
    open Scrum.Infrastructure
    open Scrum.Infrastructure.Seedwork.Json
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
    
    let runWebApp (args: string[]) =
        let builder = WebApplication.CreateBuilder(args)
        builder.Services
            .AddOptions<JwtAuthenticationSettings>()
            .BindConfiguration(JwtAuthenticationSettings.JwtAuthentication)
            .ValidateDataAnnotations()
            .ValidateOnStart()
        |> ignore

        let serviceProvider = builder.Services.BuildServiceProvider()
        let jwtAuthenticationSettings =
            serviceProvider.GetService<IOptions<JwtAuthenticationSettings>>().Value
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(fun options ->
                options.TokenValidationParameters <-
                    TokenValidationParameters(
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtAuthenticationSettings.Issuer.ToString(),
                        ValidAudience = jwtAuthenticationSettings.Audience.ToString(),
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

        builder.Services.AddCors(fun options ->
            options.AddDefaultPolicy(fun builder -> builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod() |> ignore))
        |> ignore

        builder.Services.AddHttpContextAccessor() |> ignore
        builder.Services
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

        let logger = ScrumLogger.log (serviceProvider.GetService<ILogger<_>>())                 
        let connectionString = builder.Configuration.GetConnectionString("Scrum")
        DatabaseMigrator(logger, connectionString)
            .Apply()

        builder.Services
            .AddHealthChecks()
            .AddTypeActivatedCheck<MemoryHealthCheck>("Memory", HealthStatus.Degraded, Seq.empty, args = [| int64 (5 * 1024) |])
            .AddTypeActivatedCheck<SQLiteHealthCheck>(
                "Database",
                HealthStatus.Degraded,
                Seq.empty,
                args = [| connectionString |]
            )
        |> ignore

        builder.Services.AddControllers() |> ignore
        builder.Services.AddResponseCaching() |> ignore
        builder.Services.AddEndpointsApiExplorer() |> ignore

        // Azure hosting under a Linux means the application is running a
        // container. Inside the container, the application is run using the
        // dotnet command, meaning Kestrel is serving traffic. Kestrel doesn't
        // have build-in compression support, so we add in application level
        // compression:
        // https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression.
        builder.Services
            .AddResponseCompression(fun options ->
                options.EnableForHttps <- true
                options.Providers.Add<GzipCompressionProvider>())
            .Configure<GzipCompressionProviderOptions>(fun (options: GzipCompressionProviderOptions) ->
                options.Level <- CompressionLevel.SmallestSize)
            .Configure<BrotliCompressionProviderOptions>(fun (options: BrotliCompressionProviderOptions) ->
                options.Level <- CompressionLevel.SmallestSize)
        |> ignore
        
        builder.Services.AddGiraffe() |> ignore
        
        let app = builder.Build()
        if builder.Environment.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore else ()

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
                JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true)
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
        app.UseGiraffe Routes.webApp
        app.Run()
        
    [<EntryPoint>]
    let main args =
        runWebApp args
        0
