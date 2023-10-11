namespace Scrum.Tests

open System
open System.Threading
open System.Data.SQLite
open Scrum.Application.StoryAggregateRequest.GetStoryByIdQuery
open Scrum.Web.Service
open Swensen.Unquote
open Xunit
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Application.DomainEventRequest
open Scrum.Infrastructure

module A =
    let createStoryCommand () : CreateStoryCommand = { Id = Guid.NewGuid(); Title = "title"; Description = Some "description" }

    let updateStoryCommand (source: CreateStoryCommand) = { Id = source.Id; Title = source.Title; Description = source.Description }

    let addTaskToStoryCommand () : AddTaskToStoryCommand =
        { TaskId = Guid.NewGuid()
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

    let updateTaskCommand (cmd: AddTaskToStoryCommand) =
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
            [| "delete from tasks"; "delete from stories"; "delete from domain_events" |]
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
    let userIdentityService (roles: ScrumRole list) =
        { new IUserIdentity with
            member _.GetCurrent() = ScrumIdentity.Authenticated("1", roles) }

    let fixedClock (dt: DateTime) =
        { new IClock with
            member _.CurrentUtc() = dt }

    let nullLogger =
        { new ILogger with
            member _.LogRequestPayload _ _ = ()
            member _.LogRequestDuration _ _ = ()
            member _.LogException _ = ()
            member _.LogError _ = ()
            member _.LogInformation _ = ()
            member _.LogDebug _ = () }

    let customAppEnv (roles: ScrumRole list) (clock: IClock) =
        new AppEnv(connectionString, userIdentityService roles, clock = clock, logger = nullLogger)

    let defaultFixedClock = fixedClock (DateTime(2023, 1, 1, 6, 0, 0))

    let defaultAppEnv () = customAppEnv [ Member; Admin ] defaultFixedClock

module Setup =
    let setupStoryAggregateRequests (env: IAppEnv) =
        let ct = CancellationToken.None

        // While these functions are async, we forgo the Async prefix to reduce
        // noise.
        {| CreateStory = CreateStoryCommand.runAsync env ct
           AddTaskToStory = AddTaskToStoryCommand.runAsync env ct
           GetStoryById = GetStoryByIdQuery.runAsync env ct
           DeleteStory = DeleteStoryCommand.runAsync env ct
           DeleteTask = DeleteTaskCommand.runAsync env ct
           UpdateStory = UpdateStoryCommand.runAsync env ct
           UpdateTask = UpdateTaskCommand.runAsync env ct
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
    let ``must have member role to create story`` () =
        use env = customAppEnv [ Admin ] defaultFixedClock
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.createStoryCommand ()
            let! result = fns.CreateStory storyCmd
            test <@ result = Error(CreateStoryCommand.AuthorizationError("Missing role 'member'")) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``create story with task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.createStoryCommand ()
            let! result = fns.CreateStory storyCmd
            test <@ result = Ok storyCmd.Id @>
            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! result = fns.AddTaskToStory taskCmd
            test <@ result = Ok taskCmd.TaskId @>
            let! result = fns.GetStoryById { Id = taskCmd.StoryId }

            let story: StoryDto =
                { Id = storyCmd.Id
                  Title = storyCmd.Title
                  Description = storyCmd.Description |> Option.defaultValue null
                  CreatedAt = defaultFixedClock.CurrentUtc()
                  UpdatedAt = None
                  Tasks =
                    [ { Id = taskCmd.TaskId
                        Title = taskCmd.Title
                        Description = taskCmd.Description |> Option.defaultValue null
                        CreatedAt = defaultFixedClock.CurrentUtc()
                        UpdatedAt = None } ] }

            test <@ result = Ok story @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``create duplicate story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let! result = fns.CreateStory cmd
            test <@ result = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story without task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let! result = fns.DeleteStory { Id = cmd.Id }
            test <@ result = Ok cmd.Id @>
            let! result = fns.GetStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story with task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let! result = fns.DeleteStory { Id = cmd.StoryId }
            test <@ result = Ok cmd.StoryId @>
            let! result = fns.GetStoryById { Id = cmd.StoryId }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let createStoryCmd = A.createStoryCommand ()
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = fns.CreateStory createStoryCmd
            let! _ = fns.AddTaskToStory addTaskCmd
            let! result = fns.AddTaskToStory addTaskCmd
            test <@ result = Error(AddTaskToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = { A.addTaskToStoryCommand () with StoryId = missingId () }
            let! result = fns.AddTaskToStory cmd
            test <@ result = Error(AddTaskToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! result = fns.DeleteTask cmd
            test <@ result = Ok cmd.TaskId @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``delete task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let cmd = { StoryId = missingId (); TaskId = cmd.TaskId }
            let! result = fns.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { StoryId = cmd.Id; TaskId = missingId () }
            let! result = fns.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = A.updateStoryCommand cmd
            let! result = fns.UpdateStory cmd
            test <@ result = Ok cmd.Id @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``update non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let cmd = A.updateStoryCommand cmd
            let! result = fns.UpdateStory cmd
            test <@ result = Error(UpdateStoryCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``update task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = A.updateTaskCommand cmd
            let! result = fns.UpdateTask cmd
            test <@ result = Ok cmd.TaskId @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``update non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with TaskId = missingId () }
            let! result = fns.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with StoryId = missingId () }
            let! result = fns.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

[<Collection(nameof DisableParallelization)>]
type DomainEventRequestTests() =
    do reset ()

    [<Fact>]
    let ``query domain events`` () =
        task {
            // This could be one user making a request.
            let fixedClock1 = fixedClock (DateTime(2023, 1, 1, 6, 0, 0))
            use env = customAppEnv [ Member; Admin ] fixedClock1
            let storyFns = env |> setupStoryAggregateRequests

            let storyCmd = A.createStoryCommand ()
            let! _ = storyFns.CreateStory storyCmd
            do! storyFns.Commit()

            // This could be another user making a request.
            let fixedClock2 = fixedClock (DateTime(2023, 1, 1, 7, 0, 0))
            use env = customAppEnv [ Member; Admin ] fixedClock2
            let storyFns = env |> setupStoryAggregateRequests
            let domainFns = env |> setupDomainEventRequests

            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = storyFns.AddTaskToStory taskCmd
            let! result = domainFns.GetByAggregateIdQuery { Id = storyCmd.Id }

            match result with
            | Ok r ->
                Assert.Equal(2, r.Length)
                Assert.Equal(storyCmd.Id, r[0].AggregateId)
                Assert.Equal("Story", r[0].AggregateType)
                Assert.Equal("StoryCreated", r[0].EventType)
                Assert.Equal(fixedClock1.CurrentUtc(), r[0].CreatedAt)

                Assert.Equal(storyCmd.Id, r[1].AggregateId)
                Assert.Equal("Story", r[1].AggregateType)
                Assert.Equal("TaskAddedToStory", r[1].EventType)
                Assert.Equal(fixedClock2.CurrentUtc(), r[1].CreatedAt)
            | Error e -> Assert.Fail($"%A{e}")

            do! storyFns.Commit()
        }

    [<Fact>]
    let ``must have admin role to query domain events`` () =
        use env = customAppEnv [ Member ] defaultFixedClock
        let storyFns = env |> setupStoryAggregateRequests
        let domainFns = env |> setupDomainEventRequests

        task {
            let storyCmd = A.createStoryCommand ()
            let! _ = storyFns.CreateStory storyCmd
            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = storyFns.AddTaskToStory taskCmd
            let! result = domainFns.GetByAggregateIdQuery { Id = storyCmd.Id }
            test <@ result = Error(GetByAggregateIdQuery.AuthorizationError("Missing role 'admin'")) @>
        }
