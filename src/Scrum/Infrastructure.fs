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

        let applyEventExecuteNonQuery aggregateId (cmd: SQLiteCommand) ct =
            task {
                let! count = cmd.ExecuteNonQueryAsync(ct)
                assert (count = 1)
                return aggregateId
            }

        let getLargestCreatedAtAsync table (connection: SQLiteConnection) ct =
            task {
                let sqlLast = $"select created_at from {table} order by created_at desc limit 1"
                use cmdLast = new SQLiteCommand(sqlLast, connection)
                let! last = cmdLast.ExecuteScalarAsync(ct)
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

        let offsetsToCursor globalOffset localOffset =
            if globalOffset = localOffset then
                None
            else
                localOffset
                |> string
                |> Encoding.UTF8.GetBytes
                |> Convert.ToBase64String
                |> Cursor.create
                |> panicOnError "base64"
                |> Some

open System
open System.Data.Common
open System.Text.Json
open System.Threading
open System.Collections.Generic
open System.Data.SQLite
open Microsoft.Extensions.Logging
open FsToolkit.ErrorHandling
open Scrum.Domain
open Scrum.Domain.Seedwork
open Scrum.Domain.Shared.Paging
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity
open Scrum.Application.Seedwork
open Seedwork
open Seedwork.Repository

module SqliteStoryRepository =
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

        // Dictionary doesn't maintain insertion order, so combine with ResizeArray.
        let stories = Dictionary<StoryId, Story>()
        let storyOrder = ResizeArray<StoryId>()
        let visitStory () =
            let id = r["s_id"] |> string |> Guid |> StoryId.create |> panicOnError "s_id"
            let ok, _ = stories.TryGetValue(id)
            if not ok then
                let story = parseStory id r
                stories.Add(id, story)
                storyOrder.Add(id)
            visitTask id

        task {
            while! r.ReadAsync(ct) do
               visitStory ()

            let stories =
                storyOrder
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
                    { story with Tasks = Seq.toList tasks})
                |> Seq.toList
            return stories
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
    let applyEventAsync (transaction: SQLiteTransaction) (ct: CancellationToken) now event =
        let connection = transaction.Connection
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
                    applyEventExecuteNonQuery storyId cmd ct
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
                    applyEventExecuteNonQuery storyId cmd ct
                | StoryRemoved e ->
                    let sql = "delete from stories where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let storyId = e.StoryId |> StoryId.value
                    cmd.Parameters.AddWithValue("@id", storyId |> string) |> ignore
                    applyEventExecuteNonQuery storyId cmd ct
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
                    applyEventExecuteNonQuery storyId cmd ct
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
                    applyEventExecuteNonQuery storyId cmd ct
                | TaskRemoved e ->
                    let sql = "delete from tasks where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    let storyId = e.StoryId |> StoryId.value
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", storyId |> string) |> ignore
                    applyEventExecuteNonQuery storyId cmd ct

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
                    now
        }

    let getStoriesPagedAsync (transaction: SQLiteTransaction) (ct: CancellationToken) limit cursor =
        let connection = transaction.Connection
        task {
            let sqlStories =
                "
                select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                       t.id t_id, t.story_id t_story_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                from stories s
                left join tasks t on s.id = t.story_id
                where s.created_at > @cursor
                order by s.created_at
                limit @limit"
            use cmd = new SQLiteCommand(sqlStories, connection)
            let cursor = cursorToOffset cursor
            cmd.Parameters.AddWithValue("@cursor", cursor) |> ignore
            cmd.Parameters.AddWithValue("@limit", Limit.value limit) |> ignore
            let! reader = cmd.ExecuteReaderAsync(ct)
            let! stories = storiesToDomainAsync ct reader
            let stories = stories |> List.sortBy _.Aggregate.CreatedAt

            if stories.Length = 0 then
                return { Cursor = None; Items = [] }
            else
                let pageEndOffset = stories[stories.Length - 1].Aggregate.CreatedAt.Ticks
                let! globalEndOffset = getLargestCreatedAtAsync "stories" connection ct
                let cursor = offsetsToCursor globalEndOffset pageEndOffset
                return { Cursor = cursor; Items = stories }
        }

module SqliteDomainEventRepository =
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
            use cmd = new SQLiteCommand(sql, connection, transaction)
            let cursor = cursorToOffset cursor
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
                let! globalEndOffset = getLargestCreatedAtAsync "domain_events" connection ct
                let cursor = offsetsToCursor globalEndOffset pageEndOffset
                return { Cursor = cursor; Items = events |> Seq.toList }
        }

module ScrumLogger =
    let jsonSerializationOptions =
        let o =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true)
        o.Converters.Add(Json.DateTimeJsonConverter())
        o.Converters.Add(Json.EnumJsonConverter())
        o

    let log (logger: ILogger<_>) message =
        match message with
        | Request (identity, useCase, request) ->
            let requestJson = JsonSerializer.Serialize(request, jsonSerializationOptions)
            logger.LogInformation("Use case: {useCase}, payload: {payload}, identity: {identity}", useCase, requestJson, $"%A{identity}")
        | RequestDuration (useCase, duration) ->
            logger.LogInformation("{useCase}: {duration}", useCase, duration)
        | Exception e ->
            logger.LogDebug("{exception}", $"%A{e}")
        | Err message ->
            logger.LogError(message)
        | Inf message ->
            logger.LogInformation(message)
        | Dbg message ->
            logger.LogDebug(message)
