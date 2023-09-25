namespace Scrum.Infrastructure

open System
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Scrum.Application.Seedwork
open Scrum.Domain.Seedwork
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

module Seedwork =
    module Option =
        let ofDBNull (value: obj) : obj option = if value = DBNull.Value then None else Some value

open Seedwork

type SqliteStoryRepository(transaction: SQLiteTransaction) =
    let connection = transaction.Connection
    let parseCreatedAt (v: obj) : DateTime = v |> string |> DateTime.Parse
    let parseUpdatedAt (v: obj) : DateTime option = v |> Option.ofDBNull |> Option.map (string >> DateTime.Parse)

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
                        { Root =
                            { Id = id
                              CreatedAt = parseCreatedAt r["s_created_at"]
                              UpdatedAt = parseUpdatedAt r["s_updated_at"] }
                          Title = r["s_title"] |> string |> StoryTitle
                          Description = Option.ofDBNull r["s_description"] |> Option.map (string >> StoryDescription)
                          Tasks = [] }
                    parsedStories.Add(storyId, story)
                parseTask r storyId

            // With ADO.NET, each field must be explicitly aliased for it to be part of result.
            let sql = """
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
                        let ok, tasks = parsedTasks.TryGetValue(story.Root.Id)
                        { story with Tasks = if not ok then [] else tasks.Values |> Seq.toList })
                    |> Seq.toList

                return
                    (let count = stories |> List.length
                     match count with
                     | 0 -> None
                     | 1 -> stories |> List.exactlyOne |> Some
                     | _ -> failwith $"Inconsistent database state. {count} instances with story Id: '{StoryId.value id}'")
            }

        // As we're immediately applying events to the store, we don't have to
        // worry about events evolving over time. For this domain, we don't
        // require full event sourcing; only enough event data to keep the store up to
        // date.
        member _.ApplyEventAsync (ct: CancellationToken) (event: DomainEvent) : Task<unit> =
            // TODO: persist event to DomainEvents table
            match event with
            | DomainEvent.StoryCreatedEvent e ->
                let description =
                    match e.StoryDescription with
                    | Some x -> x |> StoryDescription.value
                    | None -> null

                let sql =
                    "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                use cmd = new SQLiteCommand(sql, connection, transaction)
                let p = cmd.Parameters
                p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                p.AddWithValue("@description", description) |> ignore
                p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore

                task {
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                    return ()
                }
            | DomainEvent.StoryUpdatedEvent e -> failwith "Not implemented"
            | DomainEvent.StoryDeletedEvent e -> failwith "Not implemented"
            | DomainEvent.TaskAddedToStoryEvent e ->
                let description =
                    match e.TaskDescription with
                    | Some x -> x |> TaskDescription.value
                    | None -> null

                let sql =
                    "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                use cmd = new SQLiteCommand(sql, connection, transaction)
                let p = cmd.Parameters
                p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                p.AddWithValue("@description", description) |> ignore
                p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore

                task {
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                    return ()
                }
            | DomainEvent.TaskUpdatedEvent e -> failwith "Not implemented"
            | DomainEvent.TaskDeletedEvent e -> failwith "Not implemented"

type SystemClock() =
    interface ISystemClock with
        member this.CurrentUtc() = DateTime.UtcNow

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
    let transaction =
        lazy
            let connection = new SQLiteConnection(connectionString)
            connection.Open()
            connection.BeginTransaction()

    let storyRepository' =
        lazy (storyRepository |> Option.defaultValue (SqliteStoryRepository transaction.Value))

    let systemClock' = lazy (systemClock |> Option.defaultValue (SystemClock()))
    let logger' = lazy (logger |> Option.defaultValue (Logger()))

    interface IAppEnv with
        member _.CommitAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            if transaction.IsValueCreated then
                transaction.Value.CommitAsync(ct) // TODO: Must be awaited
            else
                Task.CompletedTask

        member _.RollbackAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            // TODO: Should we have a dispose() which calls Rollback? How to not get stuck?
            if transaction.IsValueCreated then
                transaction.Value.RollbackAsync(ct) // TODO: Must be awaited
            else
                Task.CompletedTask

        member _.SystemClock = systemClock'.Value
        member _.Logger = logger'.Value
        member _.StoryRepository = storyRepository'.Value
