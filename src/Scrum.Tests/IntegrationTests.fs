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

    let addTaskToStoryCommand () : AddTaskToStoryCommand =
        { TaskId = Guid.NewGuid()
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

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
        DeleteStoryCommand.runAsync r l ct

    [<Fact>]
    let ``create story with task`` () =
        task {
            let createStory, addTaskToStory, getStory, _ = AppEnv(connectionString) |> setupWith
            let create = A.createStoryCommand ()
            let! story = createStory create
            let add = { A.addTaskToStoryCommand () with StoryId = create.Id }
            let! task = addTaskToStory add
            let! getStory = getStory { Id = create.Id }
            return ()
        }

    [<Fact>]
    let ``create duplicate story`` () =
        task {
            let createStory, _, _, _ = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand ()
            let! story = createStory cmd
            let! story2 = createStory cmd
            test <@ story2 = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story without tasks`` () =
        task {
            let createStory, _, getStoryById, deleteStory = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand()
            let! story = createStory cmd
            let! delete = deleteStory { Id = cmd.Id }
            let! story2 = getStoryById { Id = cmd.Id }
            return ()
        }

    [<Fact>]
    let ``delete story with task`` () =
        task {
            let createStory, addTaskToStory, getStoryById, deleteStory = AppEnv(connectionString) |> setupWith
            let cmd = A.createStoryCommand()
            let! story = createStory cmd
            let add = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! task = addTaskToStory add
            let! delete = deleteStory { Id = cmd.Id }
            let! story2 = getStoryById { Id = cmd.Id }
            return ()
        }

    [<Fact>]
    let ``get story by non-existing id`` () =
        task {
            let createStory, addTaskToStory, getStory, deleteStory = AppEnv(connectionString) |> setupWith
            let! story = getStory { Id = Guid.NewGuid() }
            test <@ 42 = 42 @>
        }

    [<Fact>]
    let ``add duplicate task`` () =
        task {
            let createStory, addTaskToStory, _, _ = AppEnv(connectionString) |> setupWith
            let createStoryCmd = A.createStoryCommand ()
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = createStory createStoryCmd
            let! task = addTaskToStory addTaskCmd
            test <@ task = Ok addTaskCmd.TaskId @>
            let! task = addTaskToStory addTaskCmd
            test <@ task = Error(AddTaskToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        task {
            let _, addTaskToStory, _, _ = AppEnv(connectionString) |> setupWith
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = Guid.NewGuid() }
            let! task = addTaskToStory addTaskCmd
            test <@ task = Error(AddTaskToStoryCommand.StoryNotFound(addTaskCmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete existing task on story`` () =
        
        ()
        
    [<Fact>]
    let ``delete task on non-existing story`` () =
        ()
        
    [<Fact>]
    let ``delete non-existing task on story`` () =
        ()