namespace Scrum.Tests

open System
open System.Threading
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Swensen.Unquote
open Xunit
open Scrum.Web.Service
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Application.DomainEventRequest
open Scrum.Infrastructure

module A =
    let captureBasicStoryDetailsCommand () : CaptureBasicStoryDetailsCommand =
        { Id = Guid.NewGuid(); Title = "title"; Description = Some "description" }

    let reviseBasicStoryDetailsCommand (source: CaptureBasicStoryDetailsCommand) =
        { Id = source.Id; Title = source.Title; Description = source.Description }

    let addBasicTaskDetailsToStoryCommand () : AddBasicTaskDetailsToStoryCommand =
        { TaskId = Guid.NewGuid()
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

    let reviseBasicTaskDetailsCommand (cmd: AddBasicTaskDetailsToStoryCommand) =
        { StoryId = cmd.StoryId
          TaskId = cmd.TaskId
          Title = cmd.Title
          Description = cmd.Description }

module Database =
    // SQLite driver creates the database at the path if the file doesn't
    // already exist. The default directory is
    // tests/Scrum.Tests/bin/Debug/net8.0/scrum_test.sqlite whereas we want the
    // database at the root of the Git repository.
    let connectionString = "URI=file:../../../../../scrum_test.sqlite"

    let missingId () = Guid.NewGuid()

    // Call before a test run (from constructor), not after (from Dispose). This
    // way data is left in the database for troubleshooting.
    let reset () : unit =
        // Organize in reverse dependency order.
        let sql =
            [| "delete from tasks where id like '%'"
               "delete from stories where id like '%'"
               "delete from domain_events where id like '%'" |]
        use connection = new SQLiteConnection(connectionString)
        connection.Open()
        use transaction = connection.BeginTransaction()
        sql
        |> Array.iter (fun sql ->
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.ExecuteNonQuery() |> ignore)
        transaction.Commit()

open Database

module Setup =
    let ct = CancellationToken.None
    let nullLogger message = ()
    let clock () = DateTime.UtcNow
    let adminIdentity =
        ScrumIdentity.Authenticated(UserId = "123", Roles = [ ScrumRole.Admin ])
    let memberIdentity =
        ScrumIdentity.Authenticated(UserId = "123", Roles = [ ScrumRole.Member ])

    let getConnection (connectionString: string) : SQLiteConnection =
        let connection = new SQLiteConnection(connectionString)
        connection.Open()
        use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
        cmd.ExecuteNonQuery() |> ignore
        connection   
    
    let setupRequests () =
        let connection = getConnection connectionString
        let transaction = connection.BeginTransaction()
        let storyExist = SqliteStoryRepository.existAsync transaction ct
        let getStoryById = SqliteStoryRepository.getByIdAsync transaction ct
        let getStoriesPaged = SqliteStoryRepository.getStoriesPagedAsync transaction ct
        let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ct
        let getByAggregateId = SqliteDomainEventRepository.getByAggregateIdAsync transaction ct
        
        {| CaptureBasicStoryDetails = CaptureBasicStoryDetailsCommand.runAsync nullLogger clock storyExist storyApplyEvent
           AddBasicTaskDetailsToStory = AddBasicTaskDetailsToStoryCommand.runAsync nullLogger clock getStoryById storyApplyEvent
           RemoveStory = RemoveStoryCommand.runAsync nullLogger clock getStoryById storyApplyEvent
           RemoveTask = RemoveTaskCommand.runAsync nullLogger clock getStoryById storyApplyEvent
           GetStoryById = GetStoryByIdQuery.runAsync nullLogger getStoryById
           GetStoriesPaged = GetStoriesPagedQuery.runAsync nullLogger getStoriesPaged
           ReviseBasicStoryDetails = ReviseBasicStoryDetailsCommand.runAsync nullLogger clock getStoryById storyApplyEvent
           ReviseBasicTaskDetails = ReviseBasicTaskDetailsCommand.runAsync nullLogger clock getStoryById storyApplyEvent
           GetByAggregateId = GetByAggregateIdQuery.runAsync nullLogger getByAggregateId
           Rollback =
               fun () ->
                   transaction.Rollback()
                   transaction.Dispose()
                   connection.Dispose()
           Commit =
               fun () ->
                   transaction.Commit()
                   transaction.Dispose()
                   connection.Dispose() |}

open Setup

type ApplyDatabaseMigrationsFixture() =
    do
        // Runs before all tests.
        DatabaseMigrator(nullLogger, connectionString).Apply()

    interface IDisposable with
        member _.Dispose() =
            // Runs after all tests.
            ()

// Per https://xunit.net/docs/running-tests-in-parallel, tests in a single
// class, called a test collection, are by default run in sequence. Tests across
// multiple classes are run in parallel, with tests inside individual classes
// still running in sequence. To make a test collection span multiple classes,
// the classes must share the same collection name. In addition, we can set
// other properties on the collection, such as disabling parallelization and
// defining test collection wide setup and teardown.
//
// Marker type.
[<CollectionDefinition(nameof DisableParallelization, DisableParallelization = true)>]
type DisableParallelization() =
    interface ICollectionFixture<ApplyDatabaseMigrationsFixture>

module Helpers =
    let fail tr = Task.map (Result.mapError (fun e -> Assert.Fail($"%A{e}"))) tr

open Helpers

// Serializing integration tests makes for slower but more reliable tests. With
// SQLite, only one transaction can be in progress at once anyway. Another
// transaction will block on commit until the ongoing transaction finishes by
// committing or rolling back.
//
// Commenting out the collection attribute below may results in tests
// succeeding. But if any test assumes a reset database, tests may start failing
// because we've introduced the possibility of a race condition. For tests not to
// interfere with each other, and the reset, serialize test runs.
[<Collection(nameof DisableParallelization)>]
type StoryAggregateRequestTests() =
    do reset ()

    [<Fact>]
    let ``must have member role to create basic story details`` () =
        task {
            let fns = setupRequests ()
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! result = fns.CaptureBasicStoryDetails adminIdentity storyCmd
            test <@ result = Error(CaptureBasicStoryDetailsCommand.AuthorizationError("Missing role 'member'")) @>
            fns.Commit()
        }

    [<Fact>]
    let ``capture basic story and task details`` () =
        let fns = setupRequests ()
        taskResult {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd |> fail
            let taskCmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd |> fail
            let! r = fns.GetStoryById memberIdentity { Id = taskCmd.StoryId } |> fail

            let story =
                { Id = storyCmd.Id
                  Title = storyCmd.Title
                  Description = storyCmd.Description
                  CreatedAt = r.CreatedAt
                  UpdatedAt = None
                  Tasks =
                    [ { Id = taskCmd.TaskId
                        Title = taskCmd.Title
                        Description = taskCmd.Description
                        CreatedAt = r.Tasks[0].CreatedAt
                        UpdatedAt = None } ] }
            test <@ r = story @>
            fns.Commit()
            return! Ok()
        }

    [<Fact>]
    let ``capture duplicate story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let! result = fns.CaptureBasicStoryDetails memberIdentity cmd
            test <@ result = Error(CaptureBasicStoryDetailsCommand.DuplicateStory(cmd.Id)) @>
            fns.Commit()
        }

    [<Fact>]
    let ``remove story without task`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let! result = fns.RemoveStory memberIdentity { Id = cmd.Id }
            test <@ result = Ok cmd.Id @>
            let! result = fns.GetStoryById memberIdentity { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``remove story with task`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let! result = fns.RemoveStory memberIdentity { Id = cmd.StoryId }
            test <@ result = Ok cmd.StoryId @>
            let! result = fns.GetStoryById memberIdentity { Id = cmd.StoryId }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``add duplicate task to story`` () =
        let fns = setupRequests ()
        task {
            let createStoryCmd = A.captureBasicStoryDetailsCommand ()
            let addTaskCmd =
                { A.addBasicTaskDetailsToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = fns.CaptureBasicStoryDetails memberIdentity createStoryCmd
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity addTaskCmd
            let! result = fns.AddBasicTaskDetailsToStory memberIdentity addTaskCmd
            test <@ result = Error(AddBasicTaskDetailsToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``add task to non-existing story`` () =
        let fns = setupRequests ()
        task {
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = missingId () }
            let! result = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            test <@ result = Error(AddBasicTaskDetailsToStoryCommand.StoryNotFound(cmd.StoryId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``remove task on story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! result = fns.RemoveTask memberIdentity cmd
            test <@ result = Ok cmd.TaskId @>
            fns.Commit ()
        }
        
    [<Fact>]
    let ``remove task on non-existing story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let cmd = { StoryId = missingId (); TaskId = cmd.TaskId }
            let! result = fns.RemoveTask memberIdentity cmd
            test <@ result = Error(RemoveTaskCommand.StoryNotFound(cmd.StoryId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``remove non-existing task on story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { StoryId = cmd.Id; TaskId = missingId () }
            let! result = fns.RemoveTask memberIdentity cmd
            test <@ result = Error(RemoveTaskCommand.TaskNotFound(cmd.TaskId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``revise story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.reviseBasicStoryDetailsCommand cmd
            let! result = fns.ReviseBasicStoryDetails memberIdentity cmd
            test <@ result = Ok cmd.Id @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``revise non-existing story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let cmd = A.reviseBasicStoryDetailsCommand cmd
            let! result = fns.ReviseBasicStoryDetails memberIdentity cmd
            test <@ result = Error(ReviseBasicStoryDetailsCommand.StoryNotFound(cmd.Id)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``revise task`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = A.reviseBasicTaskDetailsCommand cmd
            let! result = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ result = Ok cmd.TaskId @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``revise non-existing task on story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = { A.reviseBasicTaskDetailsCommand cmd with TaskId = missingId () }
            let! result = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ result = Error(ReviseBasicTaskDetailsCommand.TaskNotFound(cmd.TaskId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``revise task on non-existing story`` () =
        let fns = setupRequests ()
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = { A.reviseBasicTaskDetailsCommand cmd with StoryId = missingId () }
            let! result = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ result = Error(ReviseBasicTaskDetailsCommand.StoryNotFound(cmd.StoryId)) @>
            fns.Commit()
        }
        
    [<Fact>]
    let ``get stories paged`` () =
        let fns = setupRequests ()
        taskResult {
            for i = 1 to 14 do
                let cmd = { A.captureBasicStoryDetailsCommand () with Title = $"{i}" }
                let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd |> fail
                ()

            let! page1 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = None } |> fail
            let! page2 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = page1.Cursor } |> fail
            let! page3 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = page2.Cursor } |> fail
            
            Assert.Equal(5, page1.Items.Length)
            Assert.Equal(5, page2.Items.Length)
            Assert.Equal(4, page3.Items.Length)

            let unique =
                List.concat [ page1.Items; page2.Items; page3.Items ]
                |> List.map _.Title
                |> List.distinct
                |> List.length
            Assert.Equal(14, unique)
            fns.Commit()
        }
        
[<Collection(nameof DisableParallelization)>]
type DomainEventRequestTests() =
    do reset ()
       
    [<Fact>]
    let ``query domain events`` () =
        let fns = setupRequests ()
        taskResult {
            // This could be one user making a request.
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd |> fail
            for i = 1 to 13 do
                let taskCmd =
                    { A.addBasicTaskDetailsToStoryCommand () with
                        StoryId = storyCmd.Id
                        Title = $"Title {i}" }
                let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd |> fail
                ()

            // This could be another user making a request.
            let! page1 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = None } |> fail            
            let! page2 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = page1.Cursor } |> fail
            let! page3 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = page2.Cursor } |> fail

            Assert.Equal(5, page1.Items.Length)
            Assert.Equal(5, page2.Items.Length)
            Assert.Equal(4, page3.Items.Length)

            let events = List.concat [ page1.Items; page2.Items; page3.Items ]
            Assert.Equal(14, events |> List.map _.CreatedAt |> List.distinct |> List.length)
            Assert.Equal(storyCmd.Id, (events |> List.map _.AggregateId |> List.distinct |> List.exactlyOne))
            Assert.Equal(
                "Story",
                (events
                 |> List.map _.AggregateType
                 |> List.distinct
                 |> List.exactlyOne))
            fns.Commit()
        }
        
    [<Fact>]
    let ``must have admin role to query domain events`` () =
        let fns = setupRequests ()   
        task {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd
            let taskCmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd
            let! result = fns.GetByAggregateId memberIdentity { Id = storyCmd.Id; Limit = 5; Cursor = None }
            test <@ result = Error(GetByAggregateIdQuery.AuthorizationError("Missing role 'admin'")) @>
            fns.Commit()
        }
 