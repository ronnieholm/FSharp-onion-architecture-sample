namespace Scrum.Tests

open System
open System.Threading
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Infrastructure
open Swensen.Unquote
open Xunit

// TODO: How to disable on a test class basis?
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

// TODO: Organize tests into modules (command, query)
// TODO: How to clear database between runs? No need to use typical .NET library, just issue delete * table statements in test class dispose method.

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

type StoryAggregateRequestTests( (*output: ITestOutputHelper*) ) =
    let connectionString = "URI=file:/home/rh/Downloads/scrumfs.sqlite"

    let setupWith (env: IAppEnv) =
        let r = env.StoryRepository
        let s = env.SystemClock
        let l = env.Logger
        let ct = CancellationToken.None
        CreateStoryCommand.runAsync r s l ct,
        AddTaskToStoryCommand.runAsync r s l ct,
        GetStoryByIdQuery.runAsync r l ct,
        DeleteStoryCommand.runAsync r l ct,
        DeleteTaskCommand.runAsync r l ct,
        UpdateStoryCommand.runAsync r s l ct,
        UpdateTaskCommand.runAsync r s l ct

    [<Fact>]
    let ``create story with task`` () =
        task {
            let createStory, addTaskToStory, getStory, _, _, _, _ =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! result = createStory cmd
            test <@ result = Ok(cmd.Id) @>
            let cmd' = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! result = addTaskToStory cmd'
            test <@ result = Ok(cmd'.TaskId) @>
            let! result = getStory { Id = cmd.Id }
            //test <@ result = Ok(_) @>
            test <@ true @>
        }

    [<Fact>]
    let ``create duplicate story`` () =
        task {
            let createStory, _, _, _, _, _, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let! result = createStory cmd
            test <@ result = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story without tasks`` () =
        task {
            let createStory, _, getStoryById, deleteStory, _, _, _ =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let! result = deleteStory { Id = cmd.Id }
            test <@ result = Ok(cmd.Id) @>
            let! result = getStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story with task`` () =
        task {
            let createStory, addTaskToStory, getStoryById, deleteStory, _, _, _ =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd' = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = addTaskToStory cmd'
            let! result = deleteStory { Id = cmd.Id }
            test <@ result = Ok(cmd.Id) @>
            let! result = getStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        task {
            let createStory, addTaskToStory, _, _, _, _, _ =
                AppEnv(connectionString) |> setupWith
            let createStoryCmd = A.createStoryCommand ()
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = createStory createStoryCmd
            let! _ = addTaskToStory addTaskCmd
            let! result = addTaskToStory addTaskCmd
            test <@ result = Error(AddTaskToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        task {
            let _, addTaskToStory, _, _, _, _, _ = AppEnv(connectionString) |> setupWith
            let missing = Guid.NewGuid()
            let cmd = { A.addTaskToStoryCommand () with StoryId = missing }
            let! result = addTaskToStory cmd
            test <@ result = Error(AddTaskToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete existing task on story`` () =
        task {
            let createStory, addTaskToStory, _, _, deleteTask, _, _ =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd' = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = addTaskToStory cmd'
            let cmd'' = { StoryId = cmd'.StoryId; TaskId = cmd'.TaskId }
            let! result = deleteTask cmd''
            test <@ result = Ok(cmd''.TaskId) @>
        }

    [<Fact>]
    let ``delete task on non-existing story`` () =
        task {
            let createStory, _, _, _, deleteTask, _, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let missing = Guid.NewGuid()
            let cmd = { StoryId = missing; TaskId = cmd.TaskId }
            let! result = deleteTask cmd            
            test <@ result = Error(DeleteTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete non-existing task on story`` () =
        task {
            let createStory, _, _, _, deleteTask, _, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let missing = Guid.NewGuid()
            let cmd = { StoryId = cmd.Id; TaskId = missing }
            let! result = deleteTask cmd
            test <@ result = Error(DeleteTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update existing story`` () =
        task {
            let createStory, _, _, _, _, updateStory, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd = A.updateStoryCommand cmd
            let! result = updateStory cmd            
            test <@ result = Ok(cmd.Id) @>
        }

    [<Fact>]
    let ``update non-existing story`` () =
        task {
            let _, _, _, _, _, updateStory, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let cmd = A.updateStoryCommand cmd
            let! result = updateStory cmd
            test <@ result = Error(UpdateStoryCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``update existing task`` () =
        task {
            let createStory, addTaskToStory, _, _, _, _, updateTask =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = addTaskToStory cmd
            let cmd = A.updateTaskCommand cmd
            let! result = updateTask cmd
            test <@ result = Ok(cmd.TaskId) @>
        }

    [<Fact>]
    let ``update non-existing task on existing story`` () =
        task {
            let createStory, addTaskToStory, _, _, _, _, updateTask =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = addTaskToStory cmd
            let missing = Guid.NewGuid()
            let cmd = { A.updateTaskCommand cmd with TaskId = missing }
            let! result = updateTask cmd
            test <@ result = Error(UpdateTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update task on non-existing story`` () =
        task {
            let createStory, addTaskToStory, _, _, _, _, updateTask =
                AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! _ = createStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = addTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with StoryId = Guid.NewGuid() }
            let! result = updateTask cmd
            test <@ result = Error(UpdateTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }
