module Scrum.Application

open System
open System.Threading
open FsToolkit.ErrorHandling
open Scrum.Domain
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

module Seedwork =
    type ValidationError = { Field: string; Message: string }
    module ValidationError =
        let create (field: string) (message: string) = { Field = field; Message = message }
        let mapError (field: string) : (Result<'a, string> -> Result<'a, ValidationError>) = Result.mapError (create field)

    [<Interface>]
    type ISystemClock =
        abstract CurrentUtc: unit -> DateTime

    [<Interface>]
    type ISystemClockFactory =
        abstract SystemClock: ISystemClock

    [<Interface>]
    type IStoryRepositoryFactory =
        abstract StoryRepository: IStoryRepository

    [<Interface>]
    type IAppEnv =
        inherit ISystemClockFactory
        inherit IStoryRepositoryFactory
        abstract CommitAsync: CancellationToken -> System.Threading.Tasks.Task
        abstract RollbackAsync: CancellationToken -> System.Threading.Tasks.Task

open Seedwork

module StoryAggregateRequest =
    open Seedwork

    type CreateStoryCommand = { Id: Guid; Title: string; Description: string option }

    module CreateStoryCommand =
        type CreateStoryValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: CreateStoryCommand) : Validation<CreateStoryValidatedCommand, ValidationError> =
            validation {
                let! id = StoryId.validate c.Id |> ValidationError.mapError (nameof c.Id)
                and! title = StoryTitle.create c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        StoryDescription.validate d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { Id = id; Title = title; Description = description }
            }

        type CreateStoryHandlerError =
            | ValidationErrors of ValidationError list
            | BusinessError of string
            | DuplicateStory of Guid

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (ct: CancellationToken)
            (cmd: CreateStoryCommand)
            : TaskResult<Guid, CreateStoryHandlerError> =
            taskResult {
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                do!
                    stories.ExistAsync ct cmd.Id
                    |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                let now = clock.CurrentUtc()
                let! story, event =
                    StoryAggregate.create cmd.Id cmd.Title cmd.Description now
                    |> Result.mapError BusinessError
                do! stories.ApplyEventAsync ct event
                // do! SomeOtherAggregate.Notification.SomeEventHandlerAsync dependency ct event
                return StoryId.value story.Root.Id
            }

    type AddTaskToStoryCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module AddTaskToStoryCommand =
        type AddTaskToStoryValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: AddTaskToStoryCommand) : Validation<AddTaskToStoryValidatedCommand, ValidationError> =
            validation {
                let! storyId = StoryId.validate c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.validate c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                and! title = TaskTitle.validate c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        TaskDescription.validate d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { StoryId = storyId; TaskId = taskId; Title = title; Description = description }
            }

        type AddTaskToStoryHandlerError =
            | ValidationErrors of ValidationError list
            | BusinessError of string
            | StoryNotFound of Guid

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (ct: CancellationToken)
            (cmd: AddTaskToStoryCommand)
            : TaskResult<Guid, AddTaskToStoryHandlerError> =
            taskResult {
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    stories.GetByIdAsync ct cmd.StoryId
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                let now = clock.CurrentUtc()
                let! task = create cmd.TaskId cmd.Title cmd.Description now |> Result.mapError BusinessError
                let! _, event = addTaskToStory story task now |> Result.mapError BusinessError
                do! stories.ApplyEventAsync ct event
                // do! SomeOtherAggregate.SomeEventNotificationAsync dependency ct event
                return TaskId.value task.Entity.Id
            }

    type GetStoryByIdQuery = { Id: Guid }

    module GetStoryByIdQuery =
        type GetStoryByIdValidatedQuery = { Id: StoryId }

        let validate (q: GetStoryByIdQuery) : Validation<GetStoryByIdValidatedQuery, ValidationError> =
            validation {
                let! storyId = StoryId.validate q.Id |> ValidationError.mapError (nameof q.Id)
                return { Id = storyId }
            }

        type TaskDto = { Id: Guid; Title: string; Description: string }

        module TaskDto =
            let from (task: Task) : TaskDto =
                { Id = Guid.NewGuid()
                  Title = task.Title |> TaskTitle.value
                  Description =
                    match task.Description with
                    | Some d -> d |> TaskDescription.value
                    | None -> null }

        type StoryDto =
            { Id: Guid
              Title: string
              Description: string
              CreatedAt: DateTime
              UpdatedAt: DateTime option
              Tasks: TaskDto list }

        module StoryDto =
            let from (story: Story) : StoryDto =
                { Id = story.Root.Id |> StoryId.value
                  Title = story.Title |> StoryTitle.value
                  Description =
                    // TODO: doesn't None end up being null with JSON serialization anyway?
                    story.Description
                    |> Option.map StoryDescription.value
                    |> Option.defaultValue null
                  CreatedAt = story.Root.CreatedAt
                  UpdatedAt = story.Root.UpdatedAt
                  Tasks = story.Tasks |> List.map TaskDto.from }

        type GetStoryByIdHandlerError =
            | ValidationErrors of ValidationError list
            | StoryNotFound of StoryId

        let runAsync
            (stories: IStoryRepository)
            (ct: CancellationToken)
            (qry: GetStoryByIdQuery)
            : TaskResult<StoryDto, GetStoryByIdHandlerError> =
            taskResult {
                let! qry = validate qry |> Result.mapError ValidationErrors
                let! story = stories.GetByIdAsync ct qry.Id |> TaskResult.requireSome (StoryNotFound qry.Id)
                return StoryDto.from story
            }
