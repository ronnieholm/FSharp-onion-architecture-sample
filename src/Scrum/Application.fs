module Scrum.Application

open System
open System.Diagnostics
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
    type ILogger =
        abstract LogRequest: string -> obj -> unit
        abstract LogRequestTime: string -> int -> unit

    [<Interface>]
    type ILoggerFactory =
        abstract Logger: ILogger

    [<Interface>]
    type IStoryRepositoryFactory =
        abstract StoryRepository: IStoryRepository

    [<Interface>]
    type IAppEnv =
        inherit ISystemClockFactory
        inherit ILoggerFactory
        inherit IStoryRepositoryFactory
        abstract CommitAsync: CancellationToken -> System.Threading.Tasks.Task
        abstract RollbackAsync: CancellationToken -> System.Threading.Tasks.Task

    let time (fn: unit -> 't) : 't * int (* TODO: use ms units of measure? *) =
        let sw = Stopwatch()
        sw.Start()
        let r = fn ()
        r, int sw.ElapsedMilliseconds

    let runWithDecoratorAsync (logger: ILogger) (useCase: string) (cmd: 'tcmd) (fn: unit -> TaskResult<'a, 'b>) : TaskResult<'a, 'b> =
        let result, elapsed =
            time (fun _ ->
                logger.LogRequest useCase cmd
                // TODO: don't call Result. Causes thread to block. How to await?
                taskResult { return! fn () })
        // Don't log errors as the result of evaluating fn. These are expected errors
        // which we don't want to pollute our lots with.            
        logger.LogRequestTime useCase elapsed
        result

open Seedwork

module StoryAggregateRequest =
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
            | DuplicateStory of Guid

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: CreateStoryCommand)
            : TaskResult<Guid, CreateStoryHandlerError> =
            let aux () =
                taskResult {
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    //do! Threading.Tasks.Task.Delay(5000)
                    do!
                        stories.ExistAsync ct cmd.Id
                        |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                    let now = clock.CurrentUtc()
                    let story, event = StoryAggregate.create cmd.Id cmd.Title cmd.Description now
                    do! stories.ApplyEventAsync ct event
                    // do! SomeOtherAggregate.Notification.SomeEventHandlerAsync dependency ct event
                    return StoryId.value story.Root.Id
                }

            runWithDecoratorAsync logger (nameof CreateStoryCommand) cmd aux

    type UpdateStoryCommand = { Id: Guid; Title: string; Description: string option }

    module UpdateStoryCommand =
        type UpdateStoryValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: UpdateStoryCommand) : Validation<UpdateStoryValidatedCommand, ValidationError> =
            validation {
                // Identical to create command except for return type
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

        type UpdateStoryHandlerError =
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: UpdateStoryCommand)
            : TaskResult<Guid, UpdateStoryHandlerError> =
            let aux () =
                taskResult {
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let now = clock.CurrentUtc()
                    let story, event = StoryAggregate.update story cmd.Title cmd.Description now
                    do! stories.ApplyEventAsync ct event
                    return StoryId.value story.Root.Id
                }

            runWithDecoratorAsync logger (nameof UpdateStoryCommand) cmd aux

    type DeleteStoryCommand = { Id: Guid }

    module DeleteStoryCommand =
        type DeleteStoryValidatedCommand = { Id: StoryId }

        let validate (c: DeleteStoryCommand) : Validation<DeleteStoryValidatedCommand, ValidationError> =
            validation {
                let! id = StoryId.validate c.Id |> ValidationError.mapError (nameof c.Id)
                return { Id = id }
            }

        type DeleteStoryHandlerError =
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (stories: IStoryRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: DeleteStoryCommand)
            : TaskResult<Guid, DeleteStoryHandlerError> =
            let aux () =
                taskResult {
                    // In a real-world system, not everyone should be allowed to delete. So here we'd perform authorization checks.
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let story, event = StoryAggregate.delete story
                    do! stories.ApplyEventAsync ct event
                    return StoryId.value story.Root.Id
                }

            runWithDecoratorAsync logger (nameof DeleteStoryCommand) cmd aux

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
            | StoryNotFound of Guid
            | DuplicateTask of Guid

        let fromDomainError =
            function
            | AddTaskToStoryError.DuplicateTask id -> DuplicateTask(TaskId.value id)

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: AddTaskToStoryCommand)
            : TaskResult<Guid, AddTaskToStoryHandlerError> =
            let aux () =
                taskResult {
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let now = clock.CurrentUtc()
                    let task = create cmd.TaskId cmd.Title cmd.Description now
                    let! _, event = addTaskToStory story task now |> Result.mapError fromDomainError
                    do! stories.ApplyEventAsync ct event
                    return TaskId.value task.Entity.Id
                }

            runWithDecoratorAsync logger (nameof AddTaskToStoryCommand) cmd aux

    type UpdateTaskCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module UpdateTaskCommand =
        type UpdateTaskValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: UpdateTaskCommand) : Validation<UpdateTaskValidatedCommand, ValidationError> =
            // Identical to create command except for return type
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

        type UpdateTaskHandlerError =
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainErrors =
            function
            | UpdateTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: UpdateTaskCommand)
            : TaskResult<Guid, UpdateTaskHandlerError> =
            let aux () =
                taskResult {
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let now = clock.CurrentUtc()
                    let! _, event =
                        updateTask story cmd.TaskId cmd.Title cmd.Description now
                        |> Result.mapError fromDomainErrors
                    do! stories.ApplyEventAsync ct event
                    return TaskId.value cmd.TaskId
                }

            runWithDecoratorAsync logger (nameof UpdateTaskCommand) cmd aux

    type DeleteTaskCommand = { StoryId: Guid; TaskId: Guid }

    module DeleteTaskCommand =
        type DeleteTaskValidatedCommand = { StoryId: StoryId; TaskId: TaskId }

        let validate (c: DeleteTaskCommand) : Validation<DeleteTaskValidatedCommand, ValidationError> =
            validation {
                let! storyId = StoryId.validate c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.validate c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                return { StoryId = storyId; TaskId = taskId }
            }

        type DeleteTaskHandlerError =
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainErrors =
            function
            | DeleteTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync
            (stories: IStoryRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: DeleteTaskCommand)
            : TaskResult<Guid, DeleteTaskHandlerError> =
            let aux () =
                taskResult {
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! _, event = deleteTask story cmd.TaskId |> Result.mapError fromDomainErrors
                    do! stories.ApplyEventAsync ct event
                    return TaskId.value cmd.TaskId
                }

            runWithDecoratorAsync logger (nameof DeleteTaskCommand) cmd aux

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
            (logger: ILogger)
            (ct: CancellationToken)
            (qry: GetStoryByIdQuery)
            : TaskResult<StoryDto, GetStoryByIdHandlerError> =
            let aux () =
                taskResult {
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! story = stories.GetByIdAsync ct qry.Id |> TaskResult.requireSome (StoryNotFound qry.Id)
                    return StoryDto.from story
                }

            runWithDecoratorAsync logger (nameof GetStoryByIdQuery) qry aux
