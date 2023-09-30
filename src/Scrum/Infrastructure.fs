namespace Scrum.Infrastructure

open System
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Scrum.Application.Seedwork
open Scrum.Domain
open Scrum.Domain.Seedwork
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

module Seedwork =
    module Option =
        let ofDBNull (value: obj) : obj option = if value = DBNull.Value then None else Some value

    module Repository =
        let parseCreatedAt (v: obj) : DateTime = v |> string |> DateTime.Parse
        let parseUpdatedAt (v: obj) : DateTime option = v |> Option.ofDBNull |> Option.map (string >> DateTime.Parse)

        let saveDomainEventAsync
            (connection: SQLiteConnection)
            (transaction: SQLiteTransaction)
            (ct: CancellationToken)
            (aggregateType: string) // TODO: match on type instead for robustness?
            (createdAt: DateTime)
            (event: DomainEvent)
            =
            task {
                // TODO: Maybe use SRTP to assert that event must have member StoryId of type StoryId?
                let aggregateId =
                    (match aggregateType with
                     | nameof Story ->
                         match event with
                         | StoryCreatedEvent e -> e.StoryId
                         | StoryUpdatedEvent e -> e.StoryId
                         | StoryDeletedEvent e -> e.StoryId
                         | TaskAddedToStoryEvent e -> e.StoryId
                         | TaskUpdatedEvent e -> e.StoryId
                         | TaskDeletedEvent e -> e.StoryId
                     | _ -> failwithf $"Unknown aggregate: '{aggregateType}'")
                    |> StoryId.value
                let sql =
                    "insert into domain_events (id, aggregate_type, aggregate_id, event_type, event_payload, created_at) values (@id, @aggregateType, @aggregateId, @eventType, @eventPayload, @createdAt)"
                let cmd = new SQLiteCommand(sql, connection, transaction)
                let p = cmd.Parameters
                p.AddWithValue("@id", Guid.NewGuid() |> string) |> ignore
                p.AddWithValue("@aggregateType", aggregateType) |> ignore
                p.AddWithValue("@aggregateId", aggregateId |> string) |> ignore
                p.AddWithValue("@eventType", event.GetType().Name) |> ignore
                p.AddWithValue("@eventPayload", sprintf $"%A{event}") |> ignore
                p.AddWithValue("@createdAt", createdAt |> string) |> ignore
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
            let sql = "select count(*) from stories where id = @id"
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.Parameters.AddWithValue("@id", id |> StoryId.value |> string) |> ignore
            task {
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

            // With ADO.NET, each field must be explicitly aliased for it to appears in the result.
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
                    |> Seq.map (fun story ->
                        let ok, tasks = parsedTasks.TryGetValue(story.Aggregate.Id)
                        { story with Tasks = if not ok then [] else tasks.Values |> Seq.toList })
                    |> Seq.toList

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
                do! saveDomainEventAsync connection transaction ct (nameof Story) (clock.CurrentUtc()) event
                
                match event with
                | DomainEvent.StoryCreatedEvent e ->
                    let sql =
                        "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | DomainEvent.StoryUpdatedEvent e ->
                    let sql =
                        "update stories set title = @title, description = @description, updated_at = @updatedAt where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                    p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", string e.UpdatedAt) |> ignore
                    p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | DomainEvent.StoryDeletedEvent e ->
                    let sql = "delete from stories where id = @id"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    cmd.Parameters.AddWithValue("@id", e.StoryId |> StoryId.value |> string)
                    |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    // TODO: with cascade delete of tasks, does count > 1?
                    assert (count = 1)
                | DomainEvent.TaskAddedToStoryEvent e ->
                    let sql =
                        "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | DomainEvent.TaskUpdatedEvent e ->
                    let sql =
                        "update tasks set title = @title, description = @description, updated_at = @updatedAt where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                    p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                    |> ignore
                    p.AddWithValue("@updatedAt", string e.UpdatedAt) |> ignore
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                | DomainEvent.TaskDeletedEvent e ->
                    let sql = "delete from tasks where id = @id and story_id = @storyId"
                    use cmd = new SQLiteCommand(sql, connection, transaction)
                    let p = cmd.Parameters
                    p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                    p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
            }

type Logger() =
    interface ILogger with
        member _.LogRequestPayload (useCase: string) (request: obj) : unit = printfn $"%s{useCase}: %A{request}"
        member _.LogRequestTime (useCase: string) (elapsed: uint<ms>) : unit = printfn $"%s{useCase}: %d{elapsed}"

// Pass into ctor IOptions<config>.
// Avoid Singletons dependencies as they make testing hard.
// transient dependencies are implemented like scoped factory method, except without lazy instantiation.
//member _.SomeTransientDependency = newedUpDependency
// In principle, currentTime could be a singleton, except it doesn't work well
// when we want to switch out the time provider in tests. If we kept currentTime
// a singleton changing the provider in one test would affect the others.
// Are Open and BeginTransaction on the connection idempotent?
// AppEnv is an example of the service locator pattern in use. Ideal when passing AppEnv to Notification which passes it along. Hard to accomplish with partial application. Although in OO the locater tends to be a static class. DI degenerates to service locator when classes not instantiated by framework code.
// This is our composition root: https://blog.ploeh.dk/2011/07/28/CompositionRoot/
type AppEnv(connectionString: string, ?systemClock, ?logger, ?storyRepository) =
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

    let storyRepository' =
        lazy SqliteStoryRepository(transaction.Value, systemClock'.Value)

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
        member _.StoryRepository = storyRepository'.Value
