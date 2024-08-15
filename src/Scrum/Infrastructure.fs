namespace Scrum.Infrastructure

module Seedwork =
    open System
    open System.Diagnostics
    open System.Text
    open System.Text.Json.Serialization
    open System.Threading
    open System.Data.SQLite
    open FsToolkit.ErrorHandling
    open Scrum.Domain.Shared.Paging

    exception InfrastructureException of string

    let panic message : 't = raise (InfrastructureException(message))
    let unreachable message : 't = raise (UnreachableException(message))

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

    module Option =
        let ofDBNull (value: obj) : obj option = if value = DBNull.Value then None else Some value

    module Repository =
        let panicOnError (datum: string) (result: Result<'t, _>) : 't =
            match result with
            | Ok r -> r
            | Error e ->
                // Value object create functions return string as their error
                // and entity create functions return a union member. To
                // accomodate both, we print the error using %A.
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
                let sql =
                    "insert into domain_events (id, aggregate_type, aggregate_id, event_type, event_payload, created_at) values (@id, @aggregateType, @aggregateId, @eventType, @eventPayload, @createdAt)"
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

module DatabaseMigration =
    open System
    open System.Data.SQLite
    open System.IO
    open System.Text
    open System.Reflection
    open System.Security.Cryptography
    open Scrum.Domain
    open Scrum.Application.Seedwork
    open Seedwork

    type AvailableScript = { Name: string; Hash: string; Sql: string }
    type AppliedMigration = { Name: string; Hash: string; Sql: string; CreatedAt: DateTime }

    type Migrate(log: LogMessage -> unit, connectionString: string) =
        let createMigrationsSql =
            "
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

        let verifyAppliedMigrations (available: AvailableScript array) (applied: AppliedMigration array) =
            for i = 0 to applied.Length - 1 do
                if applied[i].Name <> available[i].Name then
                    panic $"Mismatch in applied name '{applied[i].Name}' and available name '{available[i].Name}'"
                if applied[i].Hash <> available[i].Hash then
                    panic $"Mismatch in applied hash '{applied[i].Hash}' and available hash '{available[i].Hash}'"

        let applyNewMigrations (connection: SQLiteConnection) (available: AvailableScript array) (applied: AppliedMigration array) =
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

        member _.Apply() : unit =
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

module SqliteStoryRepository =
    open System
    open System.Data.Common
    open System.Threading
    open System.Collections.Generic
    open System.Data.SQLite
    open FsToolkit.ErrorHandling
    open Scrum.Domain
    open Scrum.Domain.Seedwork
    open Scrum.Domain.Shared.Paging
    open Scrum.Domain.StoryAggregate
    open Scrum.Domain.StoryAggregate.TaskEntity
    open Seedwork
    open Seedwork.Repository

    let parseStory id (r: DbDataReader) =
        // Don't parse by calling StoryAggregate.captureBasicStoryDetails as
        // in general it doesn't guarantee correct construction. When an
        // entity is a state machine, it resets the entity to its starting
        // state. Furthermore, StoryAggregate.captureBasicStoryDetails emits
        // events which we'd be discarding.
        { Aggregate = {
            Id = id
            CreatedAt = parseCreatedAt r["s_created_at"]
            UpdatedAt = parseUpdatedAt r["s_updated_at"] }
          Title = (r["s_title"] |> string |> StoryTitle.create |> panicOnError "s_title")
          Description =
            (Option.ofDBNull r["s_description"]
            |> Option.map (string >> StoryDescription.create >> panicOnError "s_description"))
          Tasks = [] }

    let parseTask id (r: DbDataReader) =
        // We know tasks are unique based on the primary key constraint in
        // the database. If we wanted to assert invariants not maintained by
        // the database, it requires explictly code. Integration tests would
        // generally catch such issues, with the exception of the database
        // updated by a migration.
        { Entity = {
            Id = id
            CreatedAt = parseCreatedAt r["t_created_at"]
            UpdatedAt = parseUpdatedAt r["t_updated_at"] }
          Title = (r["t_title"] |> string |> TaskTitle.create |> panicOnError "t_title")
          Description =
            (Option.ofDBNull r["t_description"]
            |> Option.map (string >> TaskDescription.create >> panicOnError "t_description")) }


    let storiesToDomainAsync ct (r: DbDataReader) =
        // See
        // https://github.com/ronnieholm/Playground/tree/master/FlatToTreeStructure
        // for details on the flat table to tree deserialization algorithm.
        let storyTasks = Dictionary<StoryId, Dictionary<TaskId, Task>>()
        let storyTasksOrder = Dictionary<StoryId, ResizeArray<TaskId>>()
        let visitTask storyId =
            let taskId = r["t_id"]
            if taskId <> DBNull.Value then
                let taskId = taskId |> string |> Guid |> TaskId.create |> panicOnError "t_id"
                let task = parseTask taskId r

                let storyVisited, tasks = storyTasks.TryGetValue(storyId)
                if not storyVisited then
                    // First task on the story. Mark story -> task path visited.
                    let tasks = Dictionary<_, _>()
                    tasks.Add(taskId, task)
                    storyTasks.Add(storyId, tasks)

                    let order = ResizeArray<_>()
                    order.Add(taskId)
                    storyTasksOrder.Add(storyId, order)
                else
                    // Non-first task on the story. Mark task path under
                    // existing story visited.
                    let taskVisited, _ = tasks.TryGetValue(taskId)
                    if not taskVisited then
                        tasks.Add(taskId, task)
                        let order = storyTasksOrder[storyId]
                        order.Add(taskId)

        // Dictionary doesn't maintain insertion order, so combine with
        // ResizeArray to support SQL with order clause.
        let stories = Dictionary<StoryId, Story>()
        let storiesOrder = ResizeArray<StoryId>()
        let visitStory () =
            let id = r["s_id"] |> string |> Guid |> StoryId.create |> panicOnError "s_id"
            let ok, _ = stories.TryGetValue(id)
            if not ok then
                let story = parseStory id r
                stories.Add(id, story)
                storiesOrder.Add(id)
            visitTask id

        task {
            while! r.ReadAsync(ct) do
               visitStory ()

            assert(stories.Count = storiesOrder.Count)
            assert(storyTasks.Count = storyTasksOrder.Count)

            return
                storiesOrder
                |> Seq.map (fun storyId ->
                    let story = stories[storyId]
                    let tasks =
                        let ok, order = storyTasksOrder.TryGetValue(storyId)
                        if ok then
                            order
                            |> Seq.map (fun taskId -> storyTasks[storyId][taskId])
                            |> Seq.toList
                        else
                            []
                    { story with Tasks = Seq.toList tasks })
                |> Seq.toList
        }

    let getByIdAsync (transaction: SQLiteTransaction) (ct: CancellationToken) id =
        task {
            let connection = transaction.Connection

            // For queries involving multiple tables, ADO.NET requires aliasing
            // fields for those to be extractable through the reader.
            let sql =
                "
                select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                       t.id t_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                from stories s
                left join tasks t on s.id = t.story_id
                where s.id = @id"
            use cmd = new SQLiteCommand(sql, connection)
            cmd.Parameters.AddWithValue("@id", StoryId.value id |> string) |> ignore

            // Note that ExecuteReader() returns SQLiteDataReader, but
            // ExecuteReaderAsync(...) returns DbDataReader. Perhaps because
            // querying async against SQLite, running in the same address space,
            // makes little async sense. We stick with ExecuteReaderAsync to
            // illustrate how to work with a client/server database.
            let! reader = cmd.ExecuteReaderAsync(ct)
            let! stories = storiesToDomainAsync ct reader
            return
                (let count = stories |> List.length
                 match count with
                 | 0 -> None
                 | 1 -> stories |> List.exactlyOne |> Some
                 | _ -> panic $"Invalid database. {count} instances with story Id: '{StoryId.value id}'")
        }

    let existAsync (transaction: SQLiteTransaction) (ct: CancellationToken) id =
        task {
            let connection = transaction.Connection
            let sql = "select count(*) from stories where id = @id"
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.Parameters.AddWithValue("@id", id |> StoryId.value |> string) |> ignore
            let! count = cmd.ExecuteScalarAsync(ct)
            return
                (match count :?> int64 with
                 | 0L -> false
                 | 1L -> true
                 | _ -> panic $"Invalid database. {count} instances with story Id: '{StoryId.value id}'")
        }

    // Compared to event sourcing we aren't storing commands, but events from
    // applying the commands. We don't have to worry about the shape of events
    // evolving over time; only to keep the store up to date.
    let applyEventAsync (transaction: SQLiteTransaction) (ct: CancellationToken) event =
        let connection = transaction.Connection
        task {
            let aggregateId, occuredAt =
                match event with
                | BasicStoryDetailsCaptured e ->
                    let sql =
                        "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@id", storyId |> string) |> ignore
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt
                | BasicStoryDetailsRevised e ->
                    let sql =
                        "update stories set title = @title, description = @description, updated_at = @updatedAt where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                    p.AddWithValue("@id", storyId |> string) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt
                | StoryRemoved e ->
                    let sql = "delete from stories where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let storyId = e.StoryId |> StoryId.value
                    cmd.Parameters.AddWithValue("@id", storyId |> string) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt
                | BasicTaskDetailsAddedToStory e ->
                    let sql =
                        "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", storyId |> string) |> ignore
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt
                | BasicTaskDetailsRevised e ->
                    let sql =
                        "update tasks set title = @title, description = @description, updated_at = @updatedAt where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", storyId |> string) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt
                | TaskRemoved e ->
                    let sql = "delete from tasks where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", storyId |> string) |> ignore
                    task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                    storyId, e.DomainEvent.OccurredAt

            // We don't serialize an event to JSON because F# discriminated
            // unions aren't supported by System.Text.Json
            // (https://github.com/dotnet/runtime/issues/55744). Instead of a
            // custom converter, or taking a dependency on
            // https://github.com/Tarmil/FSharp.SystemTextJson), we use the F#
            // type printer. This wouldn't work in a pure event sourced system
            // where we'd read back the event for processing, but the printer
            // suffices for persisting domain event for troubleshooting.
            do!
                persistDomainEventAsync
                    transaction
                    ct
                    (nameof Story)
                    aggregateId
                    (event.GetType().Name)
                    $"%A{event}"
                    occuredAt
        }

    let getPagedAsync (transaction: SQLiteTransaction) (ct: CancellationToken) limit cursor =
        let connection = transaction.Connection
        task {
            let sqlStories =
                "
                select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                       t.id t_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                from stories s
                left join tasks t on s.id = t.story_id
                where s.created_at > @cursor
                order by s.created_at
                limit @limit"
            let cursor = cursorToOffset cursor
            use cmd = new SQLiteCommand(sqlStories, connection)
            cmd.Parameters.AddWithValue("@cursor", cursor) |> ignore
            cmd.Parameters.AddWithValue("@limit", Limit.value limit) |> ignore
            let! reader = cmd.ExecuteReaderAsync(ct)
            let! stories = storiesToDomainAsync ct reader

            if stories.Length = 0 then
                return { Cursor = None; Items = [] }
            else
                let pageEndOffset = stories[stories.Length - 1].Aggregate.CreatedAt.Ticks
                let! globalEndOffset = getLargestCreatedAtAsync "stories" connection transaction ct
                let cursor = offsetsToCursor pageEndOffset globalEndOffset
                return { Cursor = cursor; Items = stories }
        }

module SqliteDomainEventRepository =
    open System
    open System.Threading
    open System.Data.SQLite
    open Scrum.Domain
    open Scrum.Domain.Shared.Paging
    open Scrum.Application.Seedwork
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
    open Scrum.Domain
    open Scrum.Application.Seedwork
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
