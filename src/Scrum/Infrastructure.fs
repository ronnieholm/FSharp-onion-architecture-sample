namespace Scrum.Infrastructure

open System
open System.Data.Common
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Data.SQLite
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open FsToolkit.ErrorHandling
open Scrum.Domain
open Scrum.Domain.Seedwork
open Scrum.Domain.Shared.Paging
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity
open Scrum.Application.Seedwork

module Seedwork =
    exception IntegrationException of string

    let panic (s: string) : 't = raise (IntegrationException(s))

    module Json =
        type SnakeCaseLowerNamingPolicy() =
            inherit JsonNamingPolicy()

            // SnakeCaseLower will be part of .NET 8 which releases on Nov 14,
            // 2023. After upgrading to .NET 8, remove this custom policy.
            override _.ConvertName(name: string) : string =
                (name
                 |> Seq.mapi (fun i c -> if i > 0 && Char.IsUpper(c) then $"_{c}" else $"{c}")
                 |> String.Concat)
                    .ToLower()

        type DateTimeJsonConverter() =
            inherit JsonConverter<DateTime>()

            override _.Read(_, _, _) = raise (UnreachableException())

            override _.Write(writer, value, _) =
                value.ToUniversalTime().ToString("yyy-MM-ddTHH:mm:ss.fffZ")
                |> writer.WriteStringValue

        type EnumJsonConverter() =
            inherit JsonConverter<ValueType>()

            override _.Read(_, _, _) = raise (UnreachableException())

            override _.Write(writer, value, _) =
                let t = value.GetType()
                if
                    t.IsEnum
                    || (t.IsGenericType
                        && t.GenericTypeArguments.Length = 1
                        && t.GenericTypeArguments[0].IsEnum)
                then
                    (value.ToString()
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

        let parseCreatedAt (v: obj) : DateTime = DateTime(v :?> int64, DateTimeKind.Utc)

        let parseUpdatedAt (v: obj) : DateTime option =
            v
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

        let executeNonQuery (aggregateId: Guid) (cmd: SQLiteCommand) (ct: CancellationToken) : Task<Guid> =
            task {
                let! count = cmd.ExecuteNonQueryAsync(ct)
                assert (count = 1)
                return aggregateId
            }

open Seedwork
open Seedwork.Repository

type SystemClock() =
    interface IClock with
        member _.CurrentUtc() = DateTime.UtcNow

type SqliteStoryRepository(transaction: SQLiteTransaction, clock: IClock) =
    let connection = transaction.Connection

    let toDomainAsync (ct: CancellationToken) (r: DbDataReader) : Task<Story list> =
        // See
        // https://github.com/ronnieholm/Playground/tree/master/FlatToTreeStructure
        // for details on the flat table to tree deserialization algorithm.
        let idTasks = Dictionary<StoryId, Dictionary<TaskId, Task>>()
        let toDomainTask (r: DbDataReader) (storyId: StoryId) : unit =
            let parseTaskInner id =
                (TaskEntity.create
                    id
                    (r["t_title"] |> string |> TaskTitle.create |> panicOnError "t_title")
                    (Option.ofDBNull r["t_description"]
                     |> Option.map (string >> TaskDescription.create >> panicOnError "t_description"))
                    (parseCreatedAt r["t_created_at"])
                    (parseUpdatedAt r["t_updated_at"]))

            let taskId = r["t_id"]
            if taskId <> DBNull.Value then
                let taskId = taskId |> string |> Guid |> TaskId.create |> panicOnError "t_id"
                let ok, tasks = idTasks.TryGetValue(storyId)
                if not ok then
                    let tasks = Dictionary<TaskId, Task>()
                    let task = parseTaskInner taskId
                    tasks.Add(taskId, task)
                    idTasks.Add(storyId, tasks)
                else
                    let ok, _ = tasks.TryGetValue(taskId)
                    if not ok then
                        let task = parseTaskInner taskId
                        tasks.Add(taskId, task)

        // Dictionary doesn't maintain insertion order, so when an SQL query
        // contains an "order by" clause, the dictionary will mess up ordering.
        // The caller of toDomainAsync therefore must perform a second sort. An
        // alternative would be to switch to a combination of Dictionary and
        // ResizeArray (for storing read order).
        let idStories = Dictionary<StoryId, Story>()
        let toDomain (r: DbDataReader) : unit =
            let storyId = r["s_id"] |> string |> Guid |> StoryId.create |> panicOnError "s_id"
            let ok, _ = idStories.TryGetValue(storyId)
            if not ok then
                let story, _ =
                    (StoryAggregate.captureBasicStoryDetails
                        storyId
                        (r["s_title"] |> string |> StoryTitle.create |> panicOnError "s_title")
                        (Option.ofDBNull r["s_description"]
                         |> Option.map (string >> StoryDescription.create >> panicOnError "s_description"))
                        []
                        (parseCreatedAt r["s_created_at"])
                        (parseUpdatedAt r["s_updated_at"]))
                    |> panicOnError "story"

                idStories.Add(storyId, story)
            toDomainTask r storyId

        task {
            // F# 8, to be released late Nov 14, 2023, will add while!
            // support. Following the release, clean up this code:
            // https://devblogs.microsoft.com/dotnet/simplifying-fsharp-computations-with-the-new-while-keyword
            // while! reader.ReadAsync(ct) do parseStory reader
            let mutable keepGoing = true
            while keepGoing do
                match! r.ReadAsync(ct) with
                | true -> toDomain r
                | false -> keepGoing <- false

            let stories =
                idStories.Values
                |> Seq.toList
                |> List.map (fun story ->
                    let ok, tasks = idTasks.TryGetValue(story.Aggregate.Id)
                    { story with Tasks = if not ok then [] else tasks.Values |> Seq.toList })

            // TODO: PERF: Return ResizeArray from the function.
            return stories
        }

    interface IStoryRepository with
        member _.ExistAsync (ct: CancellationToken) (id: StoryId) : Task<bool> =
            task {
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

        member _.GetByIdAsync (ct: CancellationToken) (id: StoryId) : Task<Story option> =
            task {
                // For queries involving multiple tables, ADO.NET requires aliasing
                // fields for those to be extractable through the reader.
                let sql =
                    """
                    select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                           t.id t_id, t.story_id t_story_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                    from stories s
                    left join tasks t on s.id = t.story_id
                    where s.id = @id"""
                use cmd = new SQLiteCommand(sql, connection)
                cmd.Parameters.AddWithValue("@id", StoryId.value id |> string) |> ignore

                // Note that ExecuteReader() returns SQLiteDataReader, but
                // ExecuteReaderAsync(...) returns DbDataReader. Perhaps because
                // querying async against SQLite, running in the same address
                // space, makes little async sense. We stick with
                // ExecuteReaderAsync to illustrate how to work with a
                // client/server database.
                let! reader = cmd.ExecuteReaderAsync(ct)
                let! stories = toDomainAsync ct reader
                return
                    (let count = stories |> List.length
                     match count with
                     | 0 -> None
                     | 1 -> stories |> List.exactlyOne |> Some
                     | _ -> panic $"Invalid database. {count} instances with story Id: '{StoryId.value id}'")
            }

        member _.GetStoriesPagedAsync (ct: CancellationToken) (limit: Limit) (cursor: Cursor option) : Task<Paged<Story>> =
            task {
                let cursor =
                    match cursor with
                    | Some c ->
                        Convert.FromBase64String(Cursor.value c)
                        |> Encoding.UTF8.GetString
                        |> Int64.Parse
                    | None -> 0

                let sqlLast = "select created_at from stories order by created_at desc limit 1"
                use cmdLast = new SQLiteCommand(sqlLast, connection)
                cmdLast.Parameters.AddWithValue("@cursor", cursor) |> ignore
                let! last = cmdLast.ExecuteScalarAsync(ct)
                let lastTicks = last :?> int64

                let sqlStories =
                    """
                    select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                           t.id t_id, t.story_id t_story_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                    from stories s
                    left join tasks t on s.id = t.story_id
                    where s.created_at > @cursor
                    order by s.created_at
                    limit @limit"""
                use cmd = new SQLiteCommand(sqlStories, connection)
                cmd.Parameters.AddWithValue("@cursor", cursor) |> ignore
                cmd.Parameters.AddWithValue("@limit", Limit.value limit) |> ignore
                let! reader = cmd.ExecuteReaderAsync(ct)
                let! stories = toDomainAsync ct reader
                let stories = stories |> List.sortBy (fun s -> s.Aggregate.CreatedAt)

                let lastInPageTicks = stories[stories.Length - 1].Aggregate.CreatedAt.Ticks
                let cursor =
                    if lastInPageTicks = lastTicks then
                        None
                    else
                        lastInPageTicks
                        |> string
                        |> Encoding.UTF8.GetBytes
                        |> Convert.ToBase64String
                        |> Cursor.create
                        |> panicOnError "base64"
                        |> Some
                return { Cursor = cursor; Items = stories }
            }

        // Compared to event sourcing, we immediately apply events to the store.
        // We don't have to worry about the shape of events evolving over time;
        // only to keep the store up to date.
        member _.ApplyEventAsync (ct: CancellationToken) (event: StoryDomainEvent) : Task<unit> =
            task {
                let! aggregateId =
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
                        executeNonQuery storyId cmd ct
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
                        executeNonQuery storyId cmd ct
                    | StoryRemoved e ->
                        let sql = "delete from stories where id = @id"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let storyId = e.StoryId |> StoryId.value
                        cmd.Parameters.AddWithValue("@id", storyId |> string) |> ignore
                        executeNonQuery storyId cmd ct
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
                        executeNonQuery storyId cmd ct
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
                        executeNonQuery storyId cmd ct
                    | TaskRemoved e ->
                        let sql = "delete from tasks where id = @id and story_id = @storyId"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        executeNonQuery storyId cmd ct

                // We don't serialize an event to JSON because F# discriminated
                // unions aren't supported by System.Text.Json
                // (https://github.com/dotnet/runtime/issues/55744). Instead of
                // a custom converter, or taking a dependency on
                // https://github.com/Tarmil/FSharp.SystemTextJson), we use the
                // F# type printer. This wouldn't work in a pure event sourced
                // system where we'd read back the event for processing, but the
                // printer suffices for persisting domain event for
                // troubleshooting.
                do!
                    persistDomainEventAsync
                        transaction
                        ct
                        (nameof Story)
                        aggregateId
                        (event.GetType().Name)
                        $"%A{event}"
                        (clock.CurrentUtc())
            }

type SqliteDomainEventRepository(transaction: SQLiteTransaction) =
    let connection = transaction.Connection

    interface IDomainEventRepository with
        member _.GetByAggregateIdAsync (ct: CancellationToken) (aggregateId: Guid) : Task<PersistedDomainEvent list> =
            let sql =
                "select id, aggregate_id, aggregate_type, event_type, event_payload, created_at from domain_events where aggregate_id = @aggregateId order by created_at"
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.Parameters.AddWithValue("@aggregateId", aggregateId |> string) |> ignore

            task {
                let! r = cmd.ExecuteReaderAsync(ct)
                let mutable keepGoing = true
                let events = ResizeArray<PersistedDomainEvent>()

                while keepGoing do
                    match! r.ReadAsync(ct) with
                    | true ->
                        let e =
                            { Id = r["id"] |> string |> Guid
                              AggregateId = r["aggregate_id"] |> string |> Guid
                              AggregateType = r["aggregate_type"] |> string
                              EventType = r["event_type"] |> string
                              EventPayload = r["event_payload"] |> string
                              CreatedAt = parseCreatedAt r["created_at"] }
                        events.Add(e)
                    | false -> keepGoing <- false

                return events |> Seq.toList
            }

type Logger() =
    let logger =
        use factory = new LoggerFactory()
        let logger = factory.CreateLogger<Logger>()
        logger

    static let jsonSerializationOptions =
        let o =
            JsonSerializerOptions(PropertyNamingPolicy = Json.SnakeCaseLowerNamingPolicy(), WriteIndented = true)
        o.Converters.Add(Json.DateTimeJsonConverter())
        o.Converters.Add(Json.EnumJsonConverter())
        o

    interface IScrumLogger with
        member _.LogRequestPayload (useCase: string) (request: obj) : unit =
            let json = JsonSerializer.Serialize(request, jsonSerializationOptions)
            logger.LogInformation($"%s{useCase}: %s{json}")

        member _.LogRequestDuration (useCase: string) (duration: uint<ms>) : unit = logger.LogInformation($"%s{useCase}: %d{duration}")
        member _.LogException(e: exn) : unit = logger.LogDebug($"%A{e}")
        member _.LogError(message: string) = logger.LogError(message)
        member _.LogInformation(message: string) = logger.LogInformation(message)
        member _.LogDebug(message: string) = logger.LogDebug(message)

type AppEnv
    (
        connectionString: string,
        identity: IScrumIdentity,
        ?logger: IScrumLogger,
        ?clock: IClock,
        ?stories: IStoryRepository,
        ?domainEvents: IDomainEventRepository
    ) =
    // Bind connection and transaction with a let, not a use, or repository
    // operations will fail with: "System.ObjectDisposedException: Cannot access
    // a disposed object.". Connection and transaction are unmanaged resources,
    // disposed of in the IDisposable implementation.
    let connection =
        lazy
            let connection = new SQLiteConnection(connectionString)
            connection.Open()
            use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
            cmd.ExecuteNonQuery() |> ignore
            connection

    let transaction = lazy connection.Value.BeginTransaction()
    let logger = lazy (logger |> Option.defaultValue (Logger()))
    let clock = lazy (clock |> Option.defaultValue (SystemClock()))

    let stories =
        lazy
            (stories
             |> Option.defaultValue (SqliteStoryRepository(transaction.Value, clock.Value)))

    let domainEvents =
        lazy
            (domainEvents
             |> Option.defaultValue (SqliteDomainEventRepository(transaction.Value)))

    interface IDisposable with
        member _.Dispose() =
            if transaction.IsValueCreated then
                let tx = transaction.Value
                // From
                // https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbtransaction.dispose?view=net-7.0#system-data-common-dbtransaction-dispose
                // "Dispose should rollback the transaction. However, the
                // behavior of Dispose is provider specific, and should not
                // replace calling Rollback". Yet, calling Rollback() sometimes
                // result in an exception where the transaction has no
                // connection.
                //tx.Rollback()
                tx.Dispose()
            if connection.IsValueCreated then
                connection.Value.Dispose()

    interface IAppEnv with
        member _.CommitAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            task {
                if transaction.IsValueCreated then
                    do! transaction.Value.CommitAsync(ct)
            }

        member _.RollbackAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            task {
                if transaction.IsValueCreated then
                    do! transaction.Value.RollbackAsync(ct)
            }

        member _.Identity = identity
        member _.Logger = logger.Value
        member _.Clock = clock.Value
        member _.Stories = stories.Value
        member _.DomainEvents = domainEvents.Value
