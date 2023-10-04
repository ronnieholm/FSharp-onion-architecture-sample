namespace Scrum.Infrastructure

open System
open System.Data.Common
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.Http
open Scrum.Application.Seedwork
open Scrum.Domain
open Scrum.Domain.Seedwork
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

module Seedwork =
    module Json =
        type SnakeCaseLowerNamingPolicy() =
            inherit JsonNamingPolicy()
            // SnakeCaseLower will be part of .NET 8 which releases on Nov 14, 2023.
            override _.ConvertName(name: string) : string =
                (name
                 |> Seq.mapi (fun i c -> if i > 0 && Char.IsUpper(c) then $"_{c}" else $"{c}")
                 |> String.Concat)
                    .ToLower()

        type DateTimeJsonConverter() =
            inherit JsonConverter<DateTime>()

            override this.Read(_, _, _) = raise (UnreachableException())
            override this.Write(writer, value, _) =
                value.ToUniversalTime().ToString("yyy-MM-ddTHH:mm:ss.fffZ")
                |> writer.WriteStringValue

        type EnumJsonConverter() =
            inherit JsonConverter<ValueType>()

            override this.Read(_, _, _) = raise (UnreachableException())
            override this.Write(writer, value, _) =
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

open Seedwork
open Seedwork.Repository

type SystemClock() =
    interface ISystemClock with
        member _.CurrentUtc() = DateTime.UtcNow

type SqliteStoryRepository(transaction: SQLiteTransaction, clock: ISystemClock) =
    let connection = transaction.Connection

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
                     | _ -> failwith $"Inconsistent database state. {count} instances with story Id: '{StoryId.value id}'")
            }

        member _.GetByIdAsync (ct: CancellationToken) (id: StoryId) : Task<Story option> =
            // See also: https://github.com/ronnieholm/Playground/tree/master/FlatToTreeStructure
            let parsedTasks = Dictionary<StoryId, Dictionary<TaskId, Task>>()
            let parseTask (r: DbDataReader) (storyId: StoryId) : unit =
                let parseTaskInner id =
                    { Entity =
                        { Id = id
                          CreatedAt = parseCreatedAt r["t_created_at"]
                          UpdatedAt = parseUpdatedAt r["t_updated_at"] }
                      Title = r["t_title"] |> string |> TaskTitle
                      Description = Option.ofDBNull r["t_description"] |> Option.map (string >> TaskDescription) }

                let taskId = r["t_id"]
                if taskId <> DBNull.Value then
                    let taskId = taskId |> string |> Guid |> TaskId
                    let ok, tasks = parsedTasks.TryGetValue(storyId)
                    if not ok then
                        let tasks = Dictionary<TaskId, Task>()
                        let task = parseTaskInner taskId
                        tasks.Add(taskId, task)
                        parsedTasks.Add(storyId, tasks)
                    else
                        let ok, _ = tasks.TryGetValue(taskId)
                        if not ok then
                            let task = parseTaskInner taskId
                            tasks.Add(taskId, task)

            let parsedStories = Dictionary<StoryId, Story>()
            let parseStory (r: DbDataReader) : unit =
                let storyId = r["s_id"] |> string |> Guid |> StoryId
                let ok, _ = parsedStories.TryGetValue(storyId)
                if not ok then
                    let story =
                        { Aggregate =
                            { Id = id
                              CreatedAt = parseCreatedAt r["s_created_at"]
                              UpdatedAt = parseUpdatedAt r["s_updated_at"] }
                          Title = r["s_title"] |> string |> StoryTitle
                          Description = Option.ofDBNull r["s_description"] |> Option.map (string >> StoryDescription)
                          Tasks = [] }
                    parsedStories.Add(storyId, story)
                parseTask r storyId

            // ADO.NET requires aliasing each field to extract it from the result.
            let sql =
                """
                select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                       t.id t_id, t.story_id t_story_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                from stories s
                left join tasks t on s.id = t.story_id
                where s.id = @id"""
            use cmd = new SQLiteCommand(sql, connection)
            cmd.Parameters.AddWithValue("@id", StoryId.value id |> string) |> ignore

            task {
                // Note that ExecuteReader() returns type SQLiteDataReader, but ExecuteReaderAsync(...)
                // returns type DbDataReader. Presumably because querying async against SQLite in the
                // same address space doesn't make a performance different. We stick with ExecuteReaderAsync
                // to illustrate how to work with a client/server database.
                let! reader = cmd.ExecuteReaderAsync(ct)

                // F# 8, released late 2023, adds while!
                // https://devblogs.microsoft.com/dotnet/simplifying-fsharp-computations-with-the-new-while-keyword/
                //while! reader.ReadAsync(ct) do
                //    parseStory reader
                let mutable keepGoing = true
                while keepGoing do
                    match! reader.ReadAsync(ct) with
                    | true -> parseStory reader
                    | false -> keepGoing <- false

                let stories =
                    parsedStories.Values
                    |> Seq.toList
                    |> List.map (fun story ->
                        let ok, tasks = parsedTasks.TryGetValue(story.Aggregate.Id)
                        { story with Tasks = if not ok then [] else tasks.Values |> Seq.toList })

                return
                    (let count = stories |> List.length
                     match count with
                     | 0 -> None
                     | 1 -> stories |> List.exactlyOne |> Some
                     | _ -> failwith $"Inconsistent database state. {count} instances with story Id: '{StoryId.value id}'")
            }

        // As we're immediately applying events to the store, compared to event sourcing, we don't have to
        // worry about events evolving over time. For this domain, we don't require full event sourcing;
        // only enough event data to keep the store up to date.
        member _.ApplyEventAsync (ct: CancellationToken) (event: DomainEvent) : Task<unit> =
            task {
                let aggregateId =
                    (match event with
                     | StoryCreated e -> e.StoryId
                     | StoryUpdated e -> e.StoryId
                     | StoryDeleted e -> e.StoryId
                     | TaskAddedToStory e -> e.StoryId
                     | TaskUpdated e -> e.StoryId
                     | TaskDeleted e -> e.StoryId)
                    |> StoryId.value

                // TODO: Probably move comment to saveDomainEventAsync function.
                // We don't serialize the event to JSON as F# discriminated unions aren't supported by
                // System.Text.Json (https://github.com/dotnet/runtime/issues/55744). The event is intended
                // for consumption inside application core and we therefore use F# constructs. Instead of
                // a custom converter, we use the F# type printer as a compact representation. The printer
                // wouldn't work in a pure event sourced scenario where we'd need to read back the event,
                // but saving domain event for troubleshooting, the printer suffices.
                do! persistDomainEventAsync transaction ct (nameof Story) aggregateId (event.GetType().Name) $"%A{event}" (clock.CurrentUtc())

                match event with
                | StoryCreated e ->
                    let sql =
                        "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", e.CreatedAt.Ticks) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | StoryUpdated e ->
                    let sql =
                        "update stories set title = @title, description = @description, updated_at = @updatedAt where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", e.UpdatedAt.Ticks) |> ignore
                    p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | StoryDeleted e ->
                    let sql = "delete from stories where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    cmd.Parameters.AddWithValue("@id", e.StoryId |> StoryId.value |> string)
                    |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    // TODO: with cascade delete of tasks, does count > 1?
                    assert (count = 1)
                | TaskAddedToStory e ->
                    let sql =
                        "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", e.CreatedAt.Ticks) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | TaskUpdated e ->
                    let sql =
                        "update tasks set title = @title, description = @description, updated_at = @updatedAt where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", e.UpdatedAt.Ticks) |> ignore
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | TaskDeleted e ->
                    let sql = "delete from tasks where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
            }

type SqliteDomainEventRepository(transaction: SQLiteTransaction) =
    let connection = transaction.Connection

    interface IDomainEventRepository with
        member _.GetByAggregateIdAsync (ct: CancellationToken) (aggregateId: Guid) : Task<PersistedDomainEvent list> =
            let sql =
                "select id, aggregate_id, event_type, event_payload, created_at from domain_events where aggregate_id = @aggregateId order by created_at desc"
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
                              EventType = r["event_type"] |> string
                              EventPayload = r["event_payload"] |> string
                              CreatedAt = parseCreatedAt r["created_at"] }
                        events.Add(e)
                    | false -> keepGoing <- false

                return events |> Seq.toList
            }

type Logger() =
    static let jsonSerializationOptions =
        let o =
            JsonSerializerOptions(PropertyNamingPolicy = Json.SnakeCaseLowerNamingPolicy(), WriteIndented = true)
        o.Converters.Add(Json.DateTimeJsonConverter())
        o.Converters.Add(Json.EnumJsonConverter())
        o

    interface ILogger with
        member _.LogRequestPayload (useCase: string) (request: obj) : unit =
            let json = JsonSerializer.Serialize(request, jsonSerializationOptions)
            printfn $"%s{useCase}: %s{json}"
        member _.LogRequestDuration (useCase: string) (duration: uint<ms>) : unit = printfn $"%s{useCase}: %d{duration}"
        member _.LogException(e: exn) : unit = printfn $"%A{e}"

// Pass into ctor IOptions<config>.
// Avoid Singletons dependencies as they make testing hard.
// transient dependencies are implemented like scoped factory method, except without lazy instantiation.
//member _.SomeTransientDependency = newedUpDependency
// In principle, currentTime could be a singleton, except it doesn't work well
// when we want to switch out the time provider in tests. If we kept currentTime
// a singleton changing the provider in one test would affect the others.
// Are Open and BeginTransaction on the connection idempotent?
// AppEnv is an example of the service locator pattern in use. Ideal when passing AppEnv to Notification which passes it along. Hard to accomplish with partial application. Although in OO the locator tends to be a static class. DI degenerates to service locator when classes not instantiated by framework code.
// This is our composition root: https://blog.ploeh.dk/2011/07/28/CompositionRoot/
type AppEnv(connectionString: string, userIdentity: IUserIdentity, ?systemClock: ISystemClock, ?logger: ILogger, ?storyRepository) =
    // Instantiate the connection and transaction with a let binding, and not a use binding, or
    // repository operations error will fail with:
    //
    // System.ObjectDisposedException: Cannot access a disposed object.
    //
    // The connection and transaction are disposed of by the IDisposable implementation.
    let connection = lazy new SQLiteConnection(connectionString)

    let transaction =
        lazy
            let connection = connection.Value
            connection.Open()
            connection.BeginTransaction()

    let systemClock' = lazy (systemClock |> Option.defaultValue (SystemClock()))
    let logger' = lazy (logger |> Option.defaultValue (Logger()))
    
    // Problem: How to determine the identity of the current user is dependent on the
    // hosting environment. With ASP.NET, we require the HttpContext, but other hosts
    // may use different means to satisfy the interface. How to model that with out AppEnv?
    // Passing into AppEnv the UserIdentityService? Similar in nature to connectionString
    // which would also be database specific? We could passing in the IoC container, but
    // this would make AppEnv non-explicit and be a hassle to replace this service in test
    // for instance.
    let userIdentityFactory = lazy userIdentity
    let storyRepository' =
        lazy SqliteStoryRepository(transaction.Value, systemClock'.Value)

    let domainEventRepository = lazy SqliteDomainEventRepository(transaction.Value)

    interface IDisposable with
        member _.Dispose() =
            if transaction.IsValueCreated then
                let tx = transaction.Value
                // From https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbtransaction.dispose?view=net-7.0#system-data-common-dbtransaction-dispose
                // "Dispose should rollback the transaction. However, the behavior of Dispose is provider specific, and should not replace calling Rollback".
                // TODO: How to determine if tx is in pending state? Assert this isn't the case as that indicates code forgetting to call commit/rollback.
                // TODO: Why does Rollback() sometimes result in exception where transaction has to no connection?
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

        member _.SystemClock = systemClock'.Value
        member _.Logger = logger'.Value
        member _.UserIdentity = userIdentityFactory.Value
        member _.StoryRepository = storyRepository'.Value
        member _.DomainEventRepository = domainEventRepository.Value
