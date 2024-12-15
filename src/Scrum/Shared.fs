namespace Scrum.Shared

module Domain =
    module Seedwork =
        open System

        type Entity<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
        type AggregateRoot<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
        type DomainEvent = { OccurredAt: DateTime }

        // Constraint validation on primitive types for reuse across value object
        // creation.
        module Validation =
            module Guid =
                let notEmpty value = if value = Guid.Empty then Error "Should be non-empty" else Ok value

            module String =
                let notNullOrWhitespace value =
                    if String.IsNullOrWhiteSpace(value) then
                        Error "Should be non-null, non-empty or non-whitespace"
                    else
                        Ok value

                let maxLength length (value: string) =
                    if value.Length > length then
                        Error $"Should contain less than or equal to {length} characters"
                    else
                        Ok value

            module Int =
                let between from to_ value =
                    if value < from && value > to_ then
                        Error $"Should be between {from} and {to_}, both inclusive"
                    else
                        Ok value

    open Seedwork.Validation

    module Shared =
        // As domain code isn't referencing Paging, one could argue that Paging
        // belongs in application rather than domain. That's because with F#, we
        // don't define a per aggregate IRepository in the domain. Instead, it's
        // partial functions passed into application handlers. We keep Paging in
        // domain, though, as if it was part of an explicit aggregate data access
        // interface.
        module Paging =
            type Limit = private Limit of int

            module Limit =
                let create value = value |> Int.between 1 100 |> Result.map Limit
                let value (Limit v) : int = v

            type Cursor = private Cursor of string

            module Cursor =
                let create value = value |> String.notNullOrWhitespace |> Result.map Cursor
                let value (Cursor v) : string = v

            type Paged<'t> = { Cursor: Cursor option; Items: 't list }

module Application =
    module Seedwork =
        open System
        open System.Diagnostics
        open FsToolkit.ErrorHandling

        exception ApplicationLayerException of string

        let panic message : 't = raise (ApplicationLayerException(message))

        // Contrary to outer layer's Seedwork, core doesn't define a boundary
        // exception. Core communicates errors as values.

        [<Measure>]
        type ms

        type ValidationError = { Field: string; Message: string }

        module ValidationError =
            let create field message = { Field = field; Message = message }
            let mapError field = Result.mapError (create field)

        // A pseudo-aggregate or an aggregate in the application layer. In
        // principle, we could define value types similar to those making up
        // aggregates in the domain, but for this case it's overkill. Prefixed with
        // "Persisted" to avoid confusion with domain's DomainEvent.
        type PersistedDomainEvent =
            { Id: Guid
              AggregateId: Guid
              AggregateType: string
              EventType: string
              EventPayload: string
              CreatedAt: DateTime }

        // Roles a user may possess in the application, not in Scrum as a process.
        // We're in the application layer, not domain layer, after all.
        type ScrumRole =
            | Member
            | Admin

            static member fromString s =
                match s with
                | "member" -> Member
                | "admin" -> Admin
                | unsupported -> panic $"Unsupported {nameof ScrumRole}: '{unsupported}'"

            override x.ToString() =
                match x with
                | Member -> "member"
                | Admin -> "admin"

        type ScrumIdentity =
            | Anonymous
            | Authenticated of UserId: string * Roles: ScrumRole list

        let isInRole identity role =
            match identity with
            | Anonymous -> false
            | Authenticated(_, roles) ->
                if List.contains role roles then true else false

        type LogMessage =
            // Application specific logging.
            | Workflow of ScrumIdentity * name: string * payload: obj
            | WorkflowDuration of name: string * duration: uint<ms>
            | Exception of exn
            // Delegate to .NET ILogger.
            | Err of string
            | Inf of string
            | Dbg of string

        let time (fn: unit -> 't) : 't * uint<ms> =
            let sw = Stopwatch()
            sw.Start()
            let result = fn ()
            let elapsed = (uint sw.ElapsedMilliseconds) * 1u<ms>
            result, elapsed

        // TODO: Write separate tests for the middleware.
        let runWithMiddlewareAsync<'TResponse, 'TPayload> (log: LogMessage -> unit) identity (payload: 'TPayload) (fn: unit -> System.Threading.Tasks.Task<'TResponse>) =
            try
                task {
                    let name = payload.GetType().Name
                    let result, elapsed =
                        time (fun _ ->
                            // TODO: Write test with fn that waits for x ms to make
                            // sure elapsed is correct.
                            log (Workflow(identity, name, payload))
                            fn ())
                    let! result = result
                    // Don't log errors from evaluating fn as these are expected
                    // errors. We don't want those to pollute the log with.
                    log (WorkflowDuration(name, elapsed))
                    return result
                }
            with e ->
                log (Exception(e))
                reraise ()

    module Models =
        // Per Zalando API guidelines:
        // https://opensource.zalando.com/restful-api-guidelines/#137
        type PagedDto<'t> = { Cursor: string option; Items: 't list }

    module DomainEventRequest =
        open System
        open FsToolkit.ErrorHandling
        open Domain.Seedwork
        open Domain.Shared.Paging
        open Models
        open Seedwork

        type GetByAggregateId = Guid -> Limit -> Cursor option -> System.Threading.Tasks.Task<Paged<PersistedDomainEvent>>

        type GetByAggregateIdQuery = { Id: Guid; Limit: int; Cursor: string option }

        module GetByAggregateIdQuery =
            type GetByAggregateIdValidatedQuery = { Id: Guid; Limit: Limit; Cursor: Cursor option }

            let validate (q: GetByAggregateIdQuery) =
                validation {
                    let! id = Validation.Guid.notEmpty q.Id |> ValidationError.mapError (nameof q.Id)
                    and! limit = Limit.create q.Limit |> ValidationError.mapError (nameof q.Limit)
                    and! cursor =
                        match q.Cursor with
                        | Some c -> Cursor.create c |> ValidationError.mapError (nameof q.Cursor) |> Result.map Some
                        | None -> Ok None
                    return { Id = id; Limit = limit; Cursor = cursor }
                }

            type PersistedDomainEventDto =
                { Id: Guid
                  AggregateId: Guid
                  AggregateType: string
                  EventType: string
                  EventPayload: string
                  CreatedAt: DateTime }

            module PersistedDomainEventDto =
                let from (event: PersistedDomainEvent) =
                    { Id = event.Id
                      AggregateId = event.AggregateId
                      AggregateType = event.AggregateType
                      EventType = event.EventType
                      EventPayload = event.EventPayload
                      CreatedAt = event.CreatedAt }

            type GetByAggregateIdError =
                | AuthorizationError of ScrumRole
                | ValidationErrors of ValidationError list

            let runAsync (getByAggregateId: GetByAggregateId) identity qry =
                taskResult {
                    do! isInRole identity Admin |> Result.requireTrue (AuthorizationError Admin)
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! eventsPage = getByAggregateId qry.Id qry.Limit qry.Cursor
                    return
                        { PagedDto.Cursor = eventsPage.Cursor |> Option.map Cursor.value
                          Items = eventsPage.Items |> List.map PersistedDomainEventDto.from }
                }

    module Service =
        ()

module Infrastructure =
    module Seedwork =
        open System
        open System.Diagnostics
        open System.Text
        open System.Text.Json.Serialization
        open System.Threading
        open System.Data.SQLite
        open FsToolkit.ErrorHandling
        open Domain.Shared.Paging

        exception InfrastructureException of string

        let panic message : 't = raise (InfrastructureException(message))
        let unreachable message : 't = raise (UnreachableException(message))
        let utcNow () = DateTime.UtcNow

        module Json =
            type DateTimeJsonConverter() =
                inherit JsonConverter<DateTime>()

                override _.Read(_, _, _) = unreachable "Never called"

                override _.Write(writer, value, _) =
                    value.ToUniversalTime().ToString("yyy-MM-ddTHH:mm:ss.fffZ")
                    |> writer.WriteStringValue

            type EnumJsonConverter() =
                inherit JsonConverter<ValueType>()

                override _.Read(_, _, _) = unreachable "Never called"

                override _.Write(writer, value, _) =
                    let t = value.GetType()
                    if
                        t.IsEnum
                        || (t.IsGenericType
                            && t.GenericTypeArguments.Length = 1
                            && t.GenericTypeArguments[0].IsEnum)
                    then
                        (string value
                         |> Seq.mapi (fun i c -> if i > 0 && Char.IsUpper(c) then $"_{c}" else $"{c}")
                         |> String.Concat)
                            .ToUpperInvariant()
                        |> writer.WriteStringValue

            // System.Text.Json cannot serialize an exception without throwing
            // an exception: "System.NotSupportedException: Serialization and
            // deserialization of 'System.Reflection.MethodBase' instances are
            // not supported. Path: $.Result.Exception.TargetSite.". This
            // converter works around the issue by limiting serialization to
            // the most relevant parts of the exception.
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

        module Option =
            let ofDBNull (value: obj) : obj option =
                if value = DBNull.Value then
                    None
                else
                    Some value

        module Repository =
            let getConnection (connectionString: string): SQLiteConnection =
                let connection = new SQLiteConnection(connectionString)
                connection.Open()
                use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
                cmd.ExecuteNonQuery() |> ignore
                connection

            let panicOnError (datum: string) (result: Result<'t, _>) : 't =
                match result with
                | Ok r -> r
                | Error e ->
                    // Value object create functions return string as their
                    // error and entity create functions return a union
                    // member. To accomodate both, we print the error using %A.
                    panic $"Deserialization failed for '{datum}': '%A{e}'"

            let parseCreatedAt (value: obj) = DateTime(value :?> int64, DateTimeKind.Utc)

            let parseUpdatedAt value =
                value
                |> Option.ofDBNull
                |> Option.map (fun v -> DateTime(v :?> int64, DateTimeKind.Utc))

            let persistDomainEventAsync
                (transaction: SQLiteTransaction)
                (ct: CancellationToken)
                (aggregateType: string)
                (aggregateId: Guid)
                (eventType: string)
                (payload: 't)
                (createdAt: DateTime)
                =
                task {
                    let sql = "insert into domain_events (id, aggregate_type, aggregate_id, event_type, event_payload, created_at) values (@id, @aggregateType, @aggregateId, @eventType, @eventPayload, @createdAt)"
                    let cmd = new SQLiteCommand(sql, transaction.Connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", Guid.NewGuid() |> string) |> ignore
                    p.AddWithValue("@aggregateType", aggregateType) |> ignore
                    p.AddWithValue("@aggregateId", aggregateId |> string) |> ignore
                    p.AddWithValue("@eventType", eventType) |> ignore
                    p.AddWithValue("@eventPayload", payload) |> ignore
                    p.AddWithValue("@createdAt", createdAt.Ticks) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                }

            let applyEventExecuteNonQueryAsync (cmd: SQLiteCommand) ct =
                task {
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                    return ()
                }

            let getLargestCreatedAtAsync table (connection: SQLiteConnection) (transaction: SQLiteTransaction) ct =
                task {
                    let sql = $"select created_at from {table} order by created_at desc limit 1"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let! last = cmd.ExecuteScalarAsync(ct)
                    // ExecuteScalarAsync returns null on zero rows returned.
                    return if last = null then 0L else last :?> int64
                }

            let cursorToOffset cursor =
                match cursor with
                | Some c ->
                    Convert.FromBase64String(Cursor.value c)
                    |> Encoding.UTF8.GetString
                    |> Int64.Parse
                | None -> 0

            let offsetsToCursor pageEndOffset globalEndOffset =
                if pageEndOffset = globalEndOffset then
                    None
                else
                    pageEndOffset
                    |> string
                    |> Encoding.UTF8.GetBytes
                    |> Convert.ToBase64String
                    |> Cursor.create
                    |> panicOnError "base64"
                    |> Some

        // TODO: use new framework type.
        // RFC7807 problem details format per
        // https://opensource.zalando.com/restful-api-guidelines/#176.
        type ProblemDetails2 = { Type: string; Title: string; Status: int; Detail: string }

        module ProblemDetails =
            open System.Text.Json
            open Microsoft.AspNetCore.Http
            open Microsoft.AspNetCore.Mvc
            open Microsoft.Extensions.Primitives
            open Application.Seedwork

            let create status detail: ProblemDetails2 = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

            let inferContentType (acceptHeaders: StringValues) =
                let ok =
                    acceptHeaders.ToArray()
                    |> Array.exists (fun v -> v = "application/problem+json")
                if ok then
                    "application/problem+json"
                else
                    "application/json"

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

    module DomainEventRepository =
        open System
        open System.Threading
        open System.Data.SQLite
        open Domain
        open Domain.Shared.Paging
        open Application.Seedwork
        open Seedwork.Repository

        let getByAggregateIdAsync
                (transaction: SQLiteTransaction)
                (ct: CancellationToken)
                (aggregateId: Guid)
                limit
                cursor =
            task {
                let connection = transaction.Connection
                let sql =
                    "select id, aggregate_id, aggregate_type, event_type, event_payload, created_at
                     from domain_events
                     where aggregate_id = @aggregateId
                     and created_at > @cursor
                     order by created_at
                     limit @limit"
                let cursor = cursorToOffset cursor
                use cmd = new SQLiteCommand(sql, connection, transaction)
                cmd.Parameters.AddWithValue("@aggregateId", aggregateId |> string) |> ignore
                cmd.Parameters.AddWithValue("@cursor", cursor) |> ignore
                cmd.Parameters.AddWithValue("@limit", Limit.value limit) |> ignore
                let! r = cmd.ExecuteReaderAsync(ct)
                let events = ResizeArray<PersistedDomainEvent>()

                while! r.ReadAsync(ct) do
                    let e =
                        { Id = r["id"] |> string |> Guid
                          AggregateId = r["aggregate_id"] |> string |> Guid
                          AggregateType = r["aggregate_type"] |> string
                          EventType = r["event_type"] |> string
                          EventPayload = r["event_payload"] |> string
                          CreatedAt = parseCreatedAt r["created_at"] }
                    events.Add(e)

                if events.Count = 0 then
                    return { Cursor = None; Items = [] }
                else
                    let pageEndOffset = events[events.Count - 1].CreatedAt.Ticks
                    let! globalEndOffset = getLargestCreatedAtAsync "domain_events" connection transaction ct
                    let cursor = offsetsToCursor pageEndOffset globalEndOffset
                    return { Cursor = cursor; Items = events |> Seq.toList }
            }

    module ScrumLogger =
        open System.Text.Json
        open Microsoft.Extensions.Logging
        open Application.Seedwork
        open Seedwork

        let jsonSerializationOptions =
            let o =
                JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true)
            o.Converters.Add(Json.DateTimeJsonConverter())
            o.Converters.Add(Json.EnumJsonConverter())
            o

        let log (logger: ILogger<_>) message =
            match message with
            | Workflow (identity, name, payload) ->
                let payloadJson = JsonSerializer.Serialize(payload, jsonSerializationOptions)
                logger.LogInformation("Workflow: {name}, payload: {payload}, identity: {identity}", name, payloadJson, $"%A{identity}")
            | WorkflowDuration (name, duration) ->
                logger.LogInformation("{name}: {duration}", name, duration)
            | Exception e ->
                logger.LogDebug("{exception}", $"%A{e}")
            | Err message ->
                logger.LogError(message)
            | Inf message ->
                logger.LogInformation(message)
            | Dbg message ->
                logger.LogDebug(message)

    // Claims shared between services.
    module ScrumClaims =
        let UserIdClaim = "userId"
        let RolesClaim = "roles"

    // Web specific implementation of IUserIdentity.
    module UserIdentity =
        open Microsoft.AspNetCore.Http
        open System.Security.Claims
        open Application.Seedwork

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


    module DatabaseMigration =
        open System
        open System.Data.SQLite
        open System.IO
        open System.Text
        open System.Reflection
        open System.Security.Cryptography
        open Application.Seedwork
        open Seedwork

        type AvailableScript = { Name: string; Hash: string; Sql: string }
        type AppliedMigration = { Name: string; Hash: string; Sql: string; CreatedAt: DateTime }

        type Migrate(log: LogMessage -> unit, connectionString: string) =
            let createMigrationsSql = "
                create table migrations(
                    name text primary key,
                    hash text not null,
                    sql text not null,
                    created_at integer not null
                ) strict;"

            let getAvailableScripts () =
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

            let getAppliedMigrations connection =
                let sql =
                    "select count(*) from sqlite_master where type = 'table' and name = 'migrations'"
                use cmd = new SQLiteCommand(sql, connection)
                let exist = cmd.ExecuteScalar() :?> int64

                if exist = 0 then
                    // SQLite doesn't support transactional schema changes.
                    log (Inf "Creating migrations table")
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

            let verifyAppliedMigrations (available: AvailableScript[]) (applied: AppliedMigration[]) =
                for i = 0 to applied.Length - 1 do
                    if applied[i].Name <> available[i].Name then
                        panic $"Mismatch in applied name '{applied[i].Name}' and available name '{available[i].Name}'"
                    if applied[i].Hash <> available[i].Hash then
                        panic $"Mismatch in applied hash '{applied[i].Hash}' and available hash '{available[i].Hash}'"

            let applyNewMigrations (connection: SQLiteConnection) (available: AvailableScript[]) (applied: AppliedMigration[]) =
                for i = applied.Length to available.Length - 1 do
                    // Within a transaction as we're updating the migrations table.
                    use tx = connection.BeginTransaction()
                    use cmd = new SQLiteCommand(available[i].Sql, connection, tx)
                    let count = cmd.ExecuteNonQuery()
                    assert (count >= 0)

                    let sql = $"insert into migrations ('name', 'hash', 'sql', 'created_at') values ('{available[i].Name}', '{available[i].Hash}', '{available[i].Sql}', {DateTime.UtcNow.Ticks})"
                    let cmd = new SQLiteCommand(sql, connection, tx)

                    try
                        log (Inf $"Applying migration: '{available[i].Name}'")
                        let count = cmd.ExecuteNonQuery()
                        assert (count = 1)

                        // Schema upgrade per migration code. Downgrading is
                        // unsupported.
                        match available[i].Name with
                        | "202310051903-initial" -> ()
                        | _ -> ()

                        tx.Commit()
                    with e ->
                        tx.Rollback()
                        reraise ()

            let applySeed (connection: SQLiteConnection) (seed: AvailableScript) =
                // A pseudo-migration. We don't record it in the migrations table.
                use tx = connection.BeginTransaction()
                use cmd = new SQLiteCommand(seed.Sql, connection, tx)
                try
                    log (Inf "Applying seed")
                    let count = cmd.ExecuteNonQuery()
                    assert (count >= -1)
                    tx.Commit()
                with e ->
                    tx.Rollback()
                    reraise ()

            member _.Apply() =
                use connection = new SQLiteConnection(connectionString)
                connection.Open()

                let availableScripts = getAvailableScripts ()
                let availableMigrations =
                    availableScripts |> Array.filter (fun m -> m.Name <> "seed")

                log (Inf $"Found {availableScripts.Length} available migration(s)")
                let appliedMigrations = getAppliedMigrations connection
                log (Inf $"Found {appliedMigrations.Length} applied migration(s)")

                verifyAppliedMigrations availableMigrations appliedMigrations
                applyNewMigrations connection availableMigrations appliedMigrations

                let seeds = availableScripts |> Array.filter (fun s -> s.Name = "seed")
                log (Inf $"Found {seeds.Length} seed")
                seeds |> Array.exactlyOne |> applySeed connection

    module Configuration =
        open System
        open System.ComponentModel.DataAnnotations

        type JwtAuthenticationSettings() =
            static member JwtAuthentication = nameof JwtAuthenticationSettings.JwtAuthentication
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
        open Microsoft.IdentityModel.JsonWebTokens
        open Microsoft.IdentityModel.Tokens
        open Application.Seedwork
        open Configuration

        module IdentityProvider =
            let sign (settings: JwtAuthenticationSettings) (now: DateTime) claims =
                let securityKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey))
                let credentials = SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
                let validUntil = now.AddSeconds(int settings.ExpirationInSeconds)
                let token =
                    JwtSecurityToken(
                        string settings.Issuer,
                        string settings.Audience,
                        claims,
                        expires = validUntil,
                        signingCredentials = credentials
                    )
                JwtSecurityTokenHandler().WriteToken(token)

            let issueToken (settings: JwtAuthenticationSettings) (now: DateTime) userId roles =
                // With an actual user store, we'd validate user credentials
                // here. But for this application, userId may be any string and
                // role must be either "member" or "admin".
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

module RouteHandler =
    open System
    open Microsoft.AspNetCore.Http
    open Giraffe
    open Infrastructure.Seedwork

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

    module GetPersistedDomainEvents =
        open Microsoft.Extensions.Logging
        open Microsoft.Extensions.Configuration
        open FsToolkit.ErrorHandling
        open Infrastructure
        open Application.Seedwork
        open Infrastructure.Seedwork.Repository
        open Application.DomainEventRequest
        open Application.DomainEventRequest.GetByAggregateIdQuery

        let handle aggregateId: HttpHandler =
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
                            let getByAggregateId = DomainEventRepository.getByAggregateIdAsync transaction ctx.RequestAborted

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