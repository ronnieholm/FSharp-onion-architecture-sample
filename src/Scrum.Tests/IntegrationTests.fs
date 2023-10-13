namespace Scrum.Tests

open System
open System.Threading
open System.Data.SQLite
open Scrum.Domain.StoryAggregate
open Scrum.Web.Service
open Swensen.Unquote
open Xunit
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

    let reviseBasicTaskDetailsToStoryCommand (cmd: AddBasicTaskDetailsToStoryCommand) =
        { StoryId = cmd.StoryId
          TaskId = cmd.TaskId
          Title = cmd.Title
          Description = cmd.Description }

module Database =
    // SQLite driver creates the database at the path if the file doesn't
    // already exist. The default directory is
    // src/Scrum.Tests/bin/Debug/net7.0/scrum_test.sqlite whereas we want the
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

module Fake =
    let identity (roles: ScrumRole list) =
        { new IScrumIdentity with
            member _.GetCurrent() = ScrumIdentity.Authenticated("1", roles) }

    let clock (start: DateTime) : IClock =
        let mutable calls = 0
        let count =
            fun () ->
                let r = start.AddSeconds(calls).ToUniversalTime()
                calls <- calls + 1
                r
        { new IClock with
            member _.CurrentUtc() = count () }

    let nullLogger =
        { new IScrumLogger with
            member _.LogRequestPayload _ _ = ()
            member _.LogRequestDuration _ _ = ()
            member _.LogException _ = ()
            member _.LogError _ = ()
            member _.LogInformation _ = ()
            member _.LogDebug _ = () }

    let customAppEnv (roles: ScrumRole list) (clock: IClock) =
        new AppEnv(connectionString, identity roles, clock = clock, logger = nullLogger)

    let defaultClock = clock (DateTime(2023, 1, 1, 6, 0, 0))

    let defaultAppEnv () = customAppEnv [ Member; Admin ] defaultClock

module Setup =
    let setupStoryAggregateRequests (env: IAppEnv) =
        let ct = CancellationToken.None

        // While these functions are async, we forgo the Async prefix to reduce
        // noise.
        {| CaptureStoryBasicDetails = CaptureBasicStoryDetailsCommand.runAsync env ct
           AddBasicTaskDetailsToStory = AddBasicTaskDetailsToStoryCommand.runAsync env ct
           GetStoryById = GetStoryByIdQuery.runAsync env ct
           GetStoriesPaged = GetStoriesPagedQuery.runAsync env ct
           RemoveStory = RemoveStoryCommand.runAsync env ct
           RemoveTask = RemoveTaskCommand.runAsync env ct
           ReviseBasicStoryDetails = ReviseBasicStoryDetailsCommand.runAsync env ct
           ReviseBasicTaskDetails = ReviseBasicTaskDetailsCommand.runAsync env ct
           Commit = fun _ -> env.CommitAsync ct |}

    let setupDomainEventRequests (env: IAppEnv) =
        let ct = CancellationToken.None
        {| GetByAggregateIdQuery = GetByAggregateIdQuery.runAsync env ct |}

open Fake
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
    let ``must have member role to create story basic details`` () =
        use env = customAppEnv [ Admin ] defaultClock
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! result = fns.CaptureStoryBasicDetails storyCmd
            test <@ result = Error(CaptureBasicStoryDetailsCommand.AuthorizationError("Missing role 'member'")) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``capture story and task basic details`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! result = fns.CaptureStoryBasicDetails storyCmd
            test <@ result = Ok storyCmd.Id @>
            let taskCmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = storyCmd.Id }
            let! result = fns.AddBasicTaskDetailsToStory taskCmd
            test <@ result = Ok taskCmd.TaskId @>
            let! result = fns.GetStoryById { Id = taskCmd.StoryId }
            match result with
            | Ok r ->
                let story =
                    { Id = storyCmd.Id
                      Title = storyCmd.Title
                      Description = storyCmd.Description |> Option.defaultValue null
                      CreatedAt = r.CreatedAt
                      UpdatedAt = None
                      Tasks =
                        [ { Id = taskCmd.TaskId
                            Title = taskCmd.Title
                            Description = taskCmd.Description |> Option.defaultValue null
                            CreatedAt = r.Tasks[0].CreatedAt
                            UpdatedAt = None } ] }
                test <@ r = story @>
                do! fns.Commit()
            | Error e -> Assert.Fail($"%A{e}")
        }

    [<Fact>]
    let ``capture duplicate story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let! result = fns.CaptureStoryBasicDetails cmd
            test <@ result = Error(CaptureBasicStoryDetailsCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``remove story without task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let! result = fns.RemoveStory { Id = cmd.Id }
            test <@ result = Ok cmd.Id @>
            let! result = fns.GetStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``remove story with task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory cmd
            let! result = fns.RemoveStory { Id = cmd.StoryId }
            test <@ result = Ok cmd.StoryId @>
            let! result = fns.GetStoryById { Id = cmd.StoryId }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let createStoryCmd = A.captureBasicStoryDetailsCommand ()
            let addTaskCmd =
                { A.addBasicTaskDetailsToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = fns.CaptureStoryBasicDetails createStoryCmd
            let! _ = fns.AddBasicTaskDetailsToStory addTaskCmd
            let! result = fns.AddBasicTaskDetailsToStory addTaskCmd
            test <@ result = Error(AddBasicTaskDetailsToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = missingId () }
            let! result = fns.AddBasicTaskDetailsToStory cmd
            test <@ result = Error(AddBasicTaskDetailsToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``remove task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! result = fns.RemoveTask cmd
            test <@ result = Ok cmd.TaskId @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``remove task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let cmd = { StoryId = missingId (); TaskId = cmd.TaskId }
            let! result = fns.RemoveTask cmd
            test <@ result = Error(RemoveTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``remove non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { StoryId = cmd.Id; TaskId = missingId () }
            let! result = fns.RemoveTask cmd
            test <@ result = Error(RemoveTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``revise story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = A.reviseBasicStoryDetailsCommand cmd
            let! result = fns.ReviseBasicStoryDetails cmd
            test <@ result = Ok cmd.Id @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``revise non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let cmd = A.reviseBasicStoryDetailsCommand cmd
            let! result = fns.ReviseBasicStoryDetails cmd
            test <@ result = Error(ReviseBasicStoryDetailsCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``revise task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory cmd
            let cmd = A.reviseBasicTaskDetailsToStoryCommand cmd
            let! result = fns.ReviseBasicTaskDetails cmd
            test <@ result = Ok cmd.TaskId @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``revise non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory cmd
            let cmd = { A.reviseBasicTaskDetailsToStoryCommand cmd with TaskId = missingId () }
            let! result = fns.ReviseBasicTaskDetails cmd
            test <@ result = Error(ReviseBasicTaskDetailsCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``revise task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureStoryBasicDetails cmd
            let cmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddBasicTaskDetailsToStory cmd
            let cmd = { A.reviseBasicTaskDetailsToStoryCommand cmd with StoryId = missingId () }
            let! result = fns.ReviseBasicTaskDetails cmd
            test <@ result = Error(ReviseBasicTaskDetailsCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``get stories paged`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            for i = 1 to 14 do
                let cmd = { A.captureBasicStoryDetailsCommand () with Title = $"{i}" }
                let! result = fns.CaptureStoryBasicDetails cmd
                test <@ result = Ok cmd.Id @>

            let! page1 = fns.GetStoriesPaged { Limit = 5; Cursor = None }
            match page1 with
            | Ok page1 ->
                Assert.Equal(5, page1.Items.Length)
                let! page2 = fns.GetStoriesPaged { Limit = 5; Cursor = page1.Cursor }
                match page2 with
                | Ok page2 ->
                    Assert.Equal(5, page2.Items.Length)
                    let! page3 = fns.GetStoriesPaged { Limit = 5; Cursor = page2.Cursor }
                    match page3 with
                    | Ok page3 ->
                        Assert.Equal(4, page3.Items.Length)
                        let unique =
                            List.concat [ page1.Items; page2.Items; page3.Items ]
                            |> List.map (fun s -> s.Title)
                            |> List.distinct
                            |> List.length
                        Assert.Equal(14, unique)
                    | Error _ -> Assert.Fail("Expected page 3")
                | Error _ -> Assert.Fail("Expected page 2")
            | Error _ -> Assert.Fail("Expected page 1")

            do! fns.Commit()
        }

[<Collection(nameof DisableParallelization)>]
type DomainEventRequestTests() =
    do reset ()

    [<Fact>]
    let ``query domain events`` () =
        task {
            // This could be one user making a request.
            use env = defaultAppEnv ()
            let storyFns = env |> setupStoryAggregateRequests

            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = storyFns.CaptureStoryBasicDetails storyCmd
            do! storyFns.Commit()

            // This could be another user making a request.
            use env = defaultAppEnv ()
            let storyFns = env |> setupStoryAggregateRequests
            let domainFns = env |> setupDomainEventRequests

            let taskCmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = storyFns.AddBasicTaskDetailsToStory taskCmd
            let! result = domainFns.GetByAggregateIdQuery { Id = storyCmd.Id }

            match result with
            | Ok r ->
                Assert.Equal(2, r.Length)
                Assert.Equal(storyCmd.Id, r[0].AggregateId)
                Assert.Equal("Story", r[0].AggregateType)
                Assert.Equal(nameof BasicStoryDetailsCaptured, r[0].EventType)

                Assert.Equal(storyCmd.Id, r[1].AggregateId)
                Assert.Equal("Story", r[1].AggregateType)
                Assert.Equal(nameof BasicTaskDetailsAddedToStory, r[1].EventType)

                Assert.True(r[0].CreatedAt < r[1].CreatedAt)
            | Error e -> Assert.Fail($"%A{e}")

            do! storyFns.Commit()
        }

    [<Fact>]
    let ``must have admin role to query domain events`` () =
        use env = customAppEnv [ Member ] defaultClock
        let storyFns = env |> setupStoryAggregateRequests
        let domainFns = env |> setupDomainEventRequests

        task {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = storyFns.CaptureStoryBasicDetails storyCmd
            let taskCmd = { A.addBasicTaskDetailsToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = storyFns.AddBasicTaskDetailsToStory taskCmd
            let! result = domainFns.GetByAggregateIdQuery { Id = storyCmd.Id }
            test <@ result = Error(GetByAggregateIdQuery.AuthorizationError("Missing role 'admin'")) @>
        }
