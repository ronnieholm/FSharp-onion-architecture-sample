namespace Scrum.Tests.IntegrationTests

// TODO: Use BDD test name syntax.
// TODO: Most of these tests can actually run in parallel, provided the database supports it.

// These integration tests are somewhere between unit and integration tests.
// They're intended to use an actual database because it's a real-world bug
// attractor. The tests should mock most other dependency.

open System
open System.Threading
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Swensen.Unquote
open Xunit
open FsCheck
open FsCheck.FSharp
open Scrum.Application.Seedwork
open Scrum.Application.StoryRequest
open Scrum.Application.DomainEventRequest
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity
open Scrum.Infrastructure

module A =
    let captureBasicStoryDetailsCommand () : CaptureBasicStoryDetailsCommand =
        { Id = Guid.NewGuid(); Title = "title"; Description = Some "description" }

    let reviseBasicStoryDetailsCommand (storyId: Guid) =
        { Id = storyId; Title = "title1"; Description = Some "description1" }

    let addBasicTaskDetailsToStoryCommand (storyId: Guid) : AddBasicTaskDetailsToStoryCommand =
        { StoryId = storyId
          TaskId = Guid.NewGuid()
          Title = "title"
          Description = Some "description" }

    let reviseBasicTaskDetailsCommand (storyId: Guid) (taskId: Guid): ReviseBasicTaskDetailsCommand =
        { StoryId = storyId
          TaskId = taskId
          Title = "title1"
          Description = Some "description1" }

module Database =
    // SQLite driver creates the database at the path if the file doesn't
    // already exist. The default directory is
    // tests/Scrum.Tests/bin/Debug/net8.0/scrum_test.sqlite but we want the
    // database at the root of the Git repository.
    let connectionString = "URI=file:../../../../../scrum_test.sqlite"

    let missingId () = Guid.NewGuid()

    // Call before a test run (from constructor), not after (from Dispose). This
    // way data is left in the database for troubleshooting.
    let reset () : unit =
        // Organize in reverse dependency order.
        let sql =
            [| "delete from tasks"
               "delete from stories"
               "delete from domain_events" |]
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
    let nullLogger _ = ()
    let clock () = DateTime.UtcNow
    let adminIdentity =
        Authenticated(UserId = "123", Roles = [ Admin ])
    let memberIdentity =
        Authenticated(UserId = "123", Roles = [ Member ])

    let getConnection (connectionString: string) : SQLiteConnection =
        let connection = new SQLiteConnection(connectionString)
        connection.Open()
        use cmd = new SQLiteCommand("pragma foreign_keys = on", connection)
        cmd.ExecuteNonQuery() |> ignore
        connection

    let setupRequests (transaction: SQLiteTransaction) =
        let exist = SqliteStoryRepository.existAsync transaction ct
        let getById = SqliteStoryRepository.getByIdAsync transaction ct
        let getPaged = SqliteStoryRepository.getPagedAsync transaction ct
        let applyEvent = SqliteStoryRepository.applyEventAsync transaction ct
        let getByAggregateId = SqliteDomainEventRepository.getByAggregateIdAsync transaction ct

        {| CaptureBasicStoryDetails = CaptureBasicStoryDetailsCommand.runAsync clock exist applyEvent
           AddBasicTaskDetailsToStory = AddBasicTaskDetailsToStoryCommand.runAsync clock getById applyEvent
           RemoveStory = RemoveStoryCommand.runAsync clock getById applyEvent
           RemoveTask = RemoveTaskCommand.runAsync clock getById applyEvent
           GetStoryById = GetStoryByIdQuery.runAsync getById
           GetStoriesPaged = GetStoriesPagedQuery.runAsync getPaged
           ReviseBasicStoryDetails = ReviseBasicStoryDetailsCommand.runAsync clock getById applyEvent
           ReviseBasicTaskDetails = ReviseBasicTaskDetailsCommand.runAsync clock getById applyEvent
           GetByAggregateId = GetByAggregateIdQuery.runAsync getByAggregateId |}

open Setup

type ApplyDatabaseMigrationsFixture() =
    do
        // Runs before all tests.
        DatabaseMigration.Migrate(nullLogger, connectionString).Apply()

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
    let failOnError result =
        Task.map (Result.mapError (fun e -> Assert.Fail($"%A{e}"))) result

open Helpers

// Serializing integration tests makes for slower but more reliable tests. With
// SQLite, only one transaction can be in progress at once anyway. Another
// transaction will block on commit until the ongoing transaction finishes by
// committing or rolling back.
//
// Commenting out the collection attribute below may results in tests
// succeeding. But if any test assumes a reset database, tests may start failing
// because we've introduced the possibility of a race condition. For tests not
// to interfere with each other, and the reset, serialize test runs.
[<Collection(nameof DisableParallelization)>]
type StoryRequestTests() as this =
    [<DefaultValue>] val mutable connection: SQLiteConnection
    [<DefaultValue>] val mutable transaction: SQLiteTransaction

    do
        reset ()
        this.connection <- getConnection connectionString
        this.transaction <- this.connection.BeginTransaction()

    [<Fact>]
    let ``must have member role to capture basic story details`` () =
        task {
            let fns = setupRequests this.transaction
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! result = fns.CaptureBasicStoryDetails adminIdentity cmd
            test <@ result = Error(CaptureBasicStoryDetailsCommand.AuthorizationError Member) @>
        }

    [<Fact>]
    let ``capture basic story and task details`` () =
        let fns = setupRequests this.transaction
        taskResult {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd |> failOnError
            let taskCmd = A.addBasicTaskDetailsToStoryCommand storyCmd.Id
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd |> failOnError
            let! r = fns.GetStoryById memberIdentity { Id = taskCmd.StoryId } |> failOnError

            let expected =
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
            test <@ r = expected @>
            return! Ok()
        }

    [<Fact>]
    let ``capture duplicate story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let! actual = fns.CaptureBasicStoryDetails memberIdentity cmd
            test <@ actual = Error(CaptureBasicStoryDetailsCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``remove story without task`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let! result = fns.RemoveStory memberIdentity { Id = cmd.Id }
            test <@ result = Ok cmd.Id @>
            let! result = fns.GetStoryById memberIdentity { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``remove story with task`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.addBasicTaskDetailsToStoryCommand cmd.Id
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let! actual = fns.RemoveStory memberIdentity { Id = cmd.StoryId }
            test <@ actual = Ok cmd.StoryId @>
            let! actual = fns.GetStoryById memberIdentity { Id = cmd.StoryId }
            test <@ actual = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        let fns = setupRequests this.transaction
        task {
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let taskCmd = A.addBasicTaskDetailsToStoryCommand storyCmd.Id
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd
            let! actual = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd
            test <@ actual = Error(AddBasicTaskDetailsToStoryCommand.DuplicateTask(taskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.addBasicTaskDetailsToStoryCommand (missingId ())
            let! actual = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            test <@ actual = Error(AddBasicTaskDetailsToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``remove task from story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.addBasicTaskDetailsToStoryCommand cmd.Id
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! actual = fns.RemoveTask memberIdentity cmd
            test <@ actual = Ok cmd.TaskId @>
        }

    [<Fact>]
    let ``remove task from non-existing story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = { StoryId = missingId (); TaskId = missingId () }
            let! actual = fns.RemoveTask memberIdentity cmd
            test <@ actual = Error(RemoveTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``remove non-existing task from story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = { StoryId = cmd.Id; TaskId = missingId () }
            let! actual = fns.RemoveTask memberIdentity cmd
            test <@ actual = Error(RemoveTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``revise basic story details`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.reviseBasicStoryDetailsCommand cmd.Id
            let! actual = fns.ReviseBasicStoryDetails memberIdentity cmd
            test <@ actual = Ok cmd.Id @>
        }

    [<Fact>]
    let ``revise non-existing story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let cmd = A.reviseBasicStoryDetailsCommand cmd.Id
            let! actual = fns.ReviseBasicStoryDetails memberIdentity cmd
            test <@ actual = Error(ReviseBasicStoryDetailsCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``revise basic task details`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.addBasicTaskDetailsToStoryCommand cmd.Id
            let! _ = fns.AddBasicTaskDetailsToStory memberIdentity cmd
            let cmd = A.reviseBasicTaskDetailsCommand cmd.StoryId cmd.TaskId
            let! actual = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ actual = Ok cmd.TaskId @>
        }

    [<Fact>]
    let ``revise non-existing task`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd
            let cmd = A.reviseBasicTaskDetailsCommand cmd.Id (missingId ())
            let! (actual: Result<Guid,ReviseBasicTaskDetailsCommand.ReviseBasicTaskDetailsError>) = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ actual = Error(ReviseBasicTaskDetailsCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``revise task on non-existing story`` () =
        let fns = setupRequests this.transaction
        task {
            let cmd = A.reviseBasicTaskDetailsCommand (missingId ()) (missingId ())
            let! actual = fns.ReviseBasicTaskDetails memberIdentity cmd
            test <@ actual = Error(ReviseBasicTaskDetailsCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``get stories paged`` () =
        let fns = setupRequests this.transaction
        let stories = 14
        taskResult {
            for i = 1 to stories do
                let cmd = { A.captureBasicStoryDetailsCommand () with Title = $"{i}" }
                let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd |> failOnError
                ()

            let! page1 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = None } |> failOnError
            let! page2 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = page1.Cursor } |> failOnError
            let! page3 = fns.GetStoriesPaged memberIdentity { Limit = 5; Cursor = page2.Cursor } |> failOnError

            Assert.Equal(5, page1.Items.Length)
            Assert.Equal(5, page2.Items.Length)
            Assert.Equal(4, page3.Items.Length)

            let unique =
                List.concat [ page1.Items; page2.Items; page3.Items ]
                |> List.map _.Title
                |> List.distinct
                |> List.length
            Assert.Equal(stories, unique)
        }

    interface IDisposable with
        member this.Dispose() =
            this.transaction.Commit()
            this.transaction.Dispose()
            this.connection.Dispose()

module PropertyBasedTesting =
    // 1. Define generators for commands and queries
    // 2. Setup state machine

    module Gen =
        let alphaNumericCharacter =
            Gen.elements "abcdefghijklmnopqrstuvwxyz0123456789"

        let alphaNumericNonEmptyString =
            Gen.nonEmptyListOf alphaNumericCharacter |> Gen.map (List.toArray >> String)

        let taskTitle =
            alphaNumericNonEmptyString
            |> Gen.filter (fun s ->
                s.Length <= TaskTitle.maxLength)

        let taskDescription =
            Gen.oneof [
                gen { return None }
                gen {
                    let! s =
                        alphaNumericNonEmptyString
                        |> Gen.filter (fun s ->
                            s.Length <= TaskDescription.maxLength)
                    return Some s
                }]

        let storyTitle =
            alphaNumericNonEmptyString
            |> Gen.filter (fun s ->
                s.Length <= StoryTitle.maxLength)

        let storyDescription =
            Gen.oneof [
                gen { return None }
                gen {
                    let! s =
                        alphaNumericNonEmptyString
                        |> Gen.filter (fun s ->
                            s.Length <= StoryDescription.maxLength)
                    return Some s
                }]

    module Arb =
        // Marker is required to work around a deficiency in F#. For the Check.One
        // test, where we must explicitly add arbitraries and not generators, we
        // cannot typeof<Arb> when Arb is a module, even though a module lowers to
        // a static class. We can, however, typeof<inner type on a module> and from
        // there get its parent type.
        //
        // FsCheck reflects over the arbitrary type, building a registry of
        // arbitraries, used with Check.One.
        //
        // See https://fscheck.github.io/FsCheck//RunningTests.html#Using-the-built-in-test-runner,
        // introducing the trick.
        type ArbMarker = interface end

        let captureBasicStoryDetailsCommand =
            gen {
                let! title = Gen.storyTitle
                let! description = Gen.storyDescription
                let cmd: CaptureBasicStoryDetailsCommand = { Id = Guid.NewGuid(); Title = title; Description = description }
                return cmd
            } |> Arb.fromGen

        let reviseBasicStoryDetailsCommand (cmd: CaptureBasicStoryDetailsCommand) =
            gen {
                let! title = Gen.oneof [ gen { return cmd.Title }; Gen.storyTitle ]
                let! description = Gen.oneof [ gen { return cmd.Description }; Gen.storyDescription ]
                let cmd: ReviseBasicStoryDetailsCommand = { Id = cmd.Id; Title = title; Description = description }
                return cmd
            } |> Arb.fromGen

        let addBasicTaskDetailsToStoryCommand (storyId: Guid) =
            gen {
                let! title = Gen.taskTitle
                let! description = Gen.taskDescription
                let cmd: AddBasicTaskDetailsToStoryCommand = { StoryId = storyId; TaskId = Guid.NewGuid(); Title = title; Description = description }
                return cmd
            } |> Arb.fromGen

        let reviseBasicTaskDetailsCommand (cmd: AddBasicTaskDetailsToStoryCommand) =
            gen {
                let! title = Gen.oneof [ gen { return cmd.Title }; Gen.taskTitle ]
                let! description = Gen.oneof [ gen { return cmd.Description }; Gen.taskDescription ]
                let cmd: ReviseBasicTaskDetailsCommand = { StoryId = cmd.StoryId; TaskId = cmd.TaskId; Title = title; Description = description }
                return cmd
            } |> Arb.fromGen

open PropertyBasedTesting

[<Collection(nameof DisableParallelization)>]
type StoryRequestPropertyTests() as this =
    [<DefaultValue>] val mutable connection: SQLiteConnection
    [<DefaultValue>] val mutable transaction: SQLiteTransaction

    do
        reset ()
        this.connection <- getConnection connectionString
        this.transaction <- this.connection.BeginTransaction()

    [<Fact>]
    let ``capture basic story details`` () =
        // Example of property based test, but deliberate not trying to
        // replicate "capture basic story and task details" above. When a series
        // of commands are needed, stateful property based tests are better.
        let fns = setupRequests this.transaction
        Prop.forAll
            Arb.captureBasicStoryDetailsCommand
            (fun cmd ->
                taskResult {
                    let! _ = fns.CaptureBasicStoryDetails memberIdentity cmd |> failOnError
                    let! r = fns.GetStoryById memberIdentity { Id = cmd.Id } |> failOnError
                    let expected =
                        { Id = cmd.Id
                          Title = cmd.Title
                          Description = cmd.Description
                          CreatedAt = r.CreatedAt
                          UpdatedAt = None
                          Tasks = [] }
                    test <@ r = expected @>
                    return! Ok()
                }
            )
            |> Check.QuickThrowOnFailure

    [<Fact>]
    let ``stateful property based story tests`` () =
        let x = Arb.captureBasicStoryDetailsCommand |> Arb.toGen |> Gen.sample 1

        Prop.forAll
            Arb.captureBasicStoryDetailsCommand
            (fun command ->
                let x = 10
                true)
            |> Check.QuickThrowOnFailure

    interface IDisposable with
        member this.Dispose() =
            this.transaction.Commit()
            this.transaction.Dispose()
            this.connection.Dispose()

[<Collection(nameof DisableParallelization)>]
type DomainEventRequestTests()  as this =
    [<DefaultValue>] val mutable connection: SQLiteConnection
    [<DefaultValue>] val mutable transaction: SQLiteTransaction

    do
        reset ()
        this.connection <- getConnection connectionString
        this.transaction <- this.connection.BeginTransaction()

    [<Fact>]
    let ``must have admin role to query domain events`` () =
        let fns = setupRequests this.transaction
        task {
            let! actual = fns.GetByAggregateId memberIdentity { Id = missingId (); Limit = 5; Cursor = None }
            test <@ actual = Error(GetByAggregateIdQuery.AuthorizationError Admin) @>
        }

    [<Fact>]
    let ``Get by aggregate Id paged`` () =
        let fns = setupRequests this.transaction
        taskResult {
            // This could be one user making a request.
            let storyCmd = A.captureBasicStoryDetailsCommand ()
            let! _ = fns.CaptureBasicStoryDetails memberIdentity storyCmd |> failOnError
            for i = 1 to 14 do
                let taskCmd = { A.addBasicTaskDetailsToStoryCommand  storyCmd.Id with Title = $"Title {i}" }
                let! _ = fns.AddBasicTaskDetailsToStory memberIdentity taskCmd |> failOnError
                ()

            // This could be another user making a request.
            let! page1 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = None } |> failOnError
            let! page2 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = page1.Cursor } |> failOnError
            let! page3 = fns.GetByAggregateId adminIdentity { Id = storyCmd.Id; Limit = 5; Cursor = page2.Cursor } |> failOnError

            Assert.Equal(5, page1.Items.Length)
            Assert.Equal(5, page2.Items.Length)
            Assert.Equal(5, page3.Items.Length)

            let events = List.concat [ page1.Items; page2.Items; page3.Items ]
            Assert.Equal(15, events |> List.map _.CreatedAt |> List.distinct |> List.length)
            Assert.Equal(storyCmd.Id, (events |> List.map _.AggregateId |> List.distinct |> List.exactlyOne))
            Assert.Equal(
                "Story",
                (events
                 |> List.map _.AggregateType
                 |> List.distinct
                 |> List.exactlyOne))
        }

    interface IDisposable with
        member this.Dispose() =
            this.transaction.Commit()
            this.transaction.Dispose()
            this.connection.Dispose()
