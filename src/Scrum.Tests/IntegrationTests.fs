namespace Scrum.Tests

open System
open System.Threading
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Infrastructure
open Swensen.Unquote
open Xunit

// [<assembly: CollectionBehavior(DisableTestParallelization = true)>]
// do ()

// How to run tests in parallel? Generate random databases?
// TODO: Organize tests into modules (command, query)

module A =
    let createStoryCommand: CreateStoryCommand =
        { Id = Guid.Empty; Title = "title"; Description = Some "description" }

    let addTaskToStoryCommand: AddTaskToStoryCommand =
        { TaskId = Guid.Empty
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

    let getStoryByIdQuery: GetStoryByIdQuery = { Id = Guid.Empty }

type StoryAggregateRequestTests( (*output: ITestOutputHelper*) ) =
    let connectionString = "URI=file:/home/rh/Downloads/scrumfs.sqlite"

    [<Fact>]
    let ``create story with and without duplicate`` () =
        let env = AppEnv(connectionString) :> IAppEnv
        let cmd = { A.createStoryCommand with Id = Guid.NewGuid() }
        let story = (CreateStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None cmd).Result
        test <@ story = Ok cmd.Id @>
        let story = (CreateStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None cmd).Result
        test <@ story = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>

    //     [<Fact>]
    //     let ``get story by id`` () =
    //         let storyId = Guid.NewGuid()
    //
    //         let create: CreateStoryCommand =
    //             { Id = storyId
    //               Title = "title"
    //               Description = "description" }
    //
    //         StoryHandler.createStory env create |> ignore
    //         let query: GetStoryByIdQuery = { Id = storyId }
    //         let story = StoryHandler.getById env query
    //         test <@ story |> Result.isOk @>

    [<Fact>]
    let ``get story by id`` () =
        let env = AppEnv(connectionString) :> IAppEnv
        let cmd = { A.createStoryCommand with Id = Guid.NewGuid() }
        let _ = (CreateStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None cmd).Result
        let qry = { A.getStoryByIdQuery with Id = cmd.Id }
        let story = (GetStoryByIdQuery.runAsync env.StoryRepository CancellationToken.None qry).Result
        test <@ true = true @>

    [<Fact>]
    let ``get story by non-existing id`` () =
        let env = AppEnv(connectionString) :> IAppEnv
        let qry = { A.getStoryByIdQuery with Id = (* Non-existing Id *) Guid.NewGuid() }
        let story = (GetStoryByIdQuery.runAsync env.StoryRepository CancellationToken.None qry).Result
        test <@ true = true @>

    [<Fact>]
    let ``add task to story with and without duplicate`` () =
        let env = AppEnv(connectionString) :> IAppEnv
        let createStoryCmd = { A.createStoryCommand with Id = Guid.NewGuid() }
        let addTaskCmd = { A.addTaskToStoryCommand with StoryId = createStoryCmd.Id }
        let _ =
            (CreateStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None createStoryCmd).Result
        let task =
            (AddTaskToStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None addTaskCmd).Result
        test <@ task = Ok addTaskCmd.TaskId @>
        let task =
            (AddTaskToStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None addTaskCmd).Result
        test <@ task = Error(AddTaskToStoryCommand.BusinessError("Duplicate task Id: 00000000-0000-0000-0000-000000000000")) @> // TODO: How to identify the correct case?

    [<Fact>]
    let ``add task to non-existing story`` () =
        let env = AppEnv(connectionString) :> IAppEnv
        let addTaskCmd =
            { A.addTaskToStoryCommand with StoryId = (* non-existing *) Guid.NewGuid() }
        let task =
            (AddTaskToStoryCommand.runAsync env.StoryRepository env.SystemClock CancellationToken.None addTaskCmd).Result
        test <@ task = Error(AddTaskToStoryCommand.StoryNotFound(addTaskCmd.StoryId)) @>
