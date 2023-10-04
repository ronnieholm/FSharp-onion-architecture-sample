module Scrum.Application

open System
open System.Diagnostics
open System.Threading
open FsToolkit.ErrorHandling
open Scrum.Domain
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

module Seedwork =
    [<Measure>]
    type ms

    type ValidationError = { Field: string; Message: string }

    module ValidationError =
        let create (field: string) (message: string) = { Field = field; Message = message }
        let mapError (field: string) : (Result<'a, string> -> Result<'a, ValidationError>) = Result.mapError (create field)

    // A pseudo-aggregate or an aggregate in the application layer. In
    // principle, we could define value types similar to those making up
    // aggregates in the domain, but for this case it's overkill.
    type PersistedDomainEvent =
        { Id: Guid
          AggregateId: Guid
          EventType: string
          EventPayload: string
          CreatedAt: DateTime }

    type ScrumRole =
        | Member
        | Admin

        static member FromString =
            function
            | "member" -> Member
            | "admin" -> Admin
            | unsupported -> failwith $"Unsupported {nameof ScrumRole}: '{unsupported}'"
        override x.ToString() =
            match x with
            | Member -> "member"
            | Admin -> "admin"

    type ScrumIdentity =
        | Anonymous
        | Authenticated of UserId: string * Role: ScrumRole list

    [<Interface>]
    type IUserIdentity =
        abstract GetCurrent: unit -> ScrumIdentity

    // The factory interfaces is there as a means of groping inside AppEnv.
    // Without factory interfaces, AppEnv would implement all interfaces at the
    // same same level, e.g., AppEnv.GetCurrent. With factory interfaces it
    // becomes AppEnv.UserIdentity.GetCurrent. The latter means we can inject
    // the dependency into another type by passing in AppEnv.UserIdentity.
    [<Interface>]
    type IUserIdentityFactory =
        abstract UserIdentity: IUserIdentity

    [<Interface>]
    type ISystemClock =
        abstract CurrentUtc: unit -> DateTime

    [<Interface>]
    type ISystemClockFactory =
        abstract SystemClock: ISystemClock

    [<Interface>]
    type ILogger =
        // Application specific logging.
        abstract LogRequestPayload: string -> obj -> unit
        abstract LogRequestDuration: string -> uint<ms> -> unit
        abstract LogException: exn -> unit
        // Delegates to .NET's ILogger.
        abstract LogError: string -> unit
        abstract LogInformation: string -> unit
        abstract LogDebug: string -> unit

    [<Interface>]
    type ILoggerFactory =
        abstract Logger: ILogger

    [<Interface>]
    type IStoryRepositoryFactory =
        abstract StoryRepository: IStoryRepository

    [<Interface>]
    // Mirroring the PersistedDomainEvent type, its repository is defined in the
    // application layer. While domain events are part of the domain, persisted
    // domain events aren't. After an event is applied, it's no longer needed.
    // We persist domain events for troubleshooting only.
    type IDomainEventRepository =
        abstract GetByAggregateIdAsync: CancellationToken -> Guid -> System.Threading.Tasks.Task<PersistedDomainEvent list>

    [<Interface>]
    type IDomainEventRepositoryFactory =
        abstract DomainEventRepository: IDomainEventRepository

    [<Interface>]
    type IAppEnv =
        inherit ISystemClockFactory
        inherit ILoggerFactory
        inherit IUserIdentityFactory
        inherit IStoryRepositoryFactory
        inherit IDomainEventRepositoryFactory
        inherit IDisposable
        abstract CommitAsync: CancellationToken -> System.Threading.Tasks.Task
        abstract RollbackAsync: CancellationToken -> System.Threading.Tasks.Task

    let time (fn: unit -> 't) : 't * uint<ms> =
        let sw = Stopwatch()
        sw.Start()
        let result = fn ()
        let elapsed = (uint sw.ElapsedMilliseconds) * 1u<ms>
        result, elapsed

    let runWithDecoratorAsync (logger: ILogger) (useCase: string) (cmd: 't) (fn: unit -> TaskResult<'a, 'b>) : TaskResult<'a, 'b> =
        let result, elapsed =
            time (fun _ ->
                logger.LogRequestPayload useCase cmd
                taskResult { return! fn () })
        // Don't log errors from evaluating fn as These are expected errors
        // which we don't want to pollute the log with.
        logger.LogRequestDuration useCase elapsed
        result

    let isInRole (identity: IUserIdentity) (role: ScrumRole) : Result<unit, string> =
        match identity.GetCurrent() with
        | Anonymous -> Error("Anonymous user unsupported")
        | Authenticated(_, roles) ->
            if List.contains role roles then
                Ok()
            else
                Error($"Missing role '{role.ToString()}'")

module SharedModels =
    // Data transfer objects shared across queries across aggregates.
    ()

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

        type CreateStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | DuplicateStory of Guid

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: CreateStoryCommand)
            : TaskResult<Guid, CreateStoryError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    do!
                        stories.ExistAsync ct cmd.Id
                        |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                    let now = clock.CurrentUtc()
                    let story, event = StoryAggregate.create cmd.Id cmd.Title cmd.Description now
                    do! stories.ApplyEventAsync ct event
                    // Example of publishing the StoryCreated domain event to
                    // another aggregate: do!
                    // SomeOtherAggregate.SomeEventNotificationAsync
                    // dependencies ct event
                    return StoryId.value story.Aggregate.Id
                }

            runWithDecoratorAsync logger (nameof CreateStoryCommand) cmd aux

    type UpdateStoryCommand = { Id: Guid; Title: string; Description: string option }

    module UpdateStoryCommand =
        type UpdateStoryValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: UpdateStoryCommand) : Validation<UpdateStoryValidatedCommand, ValidationError> =
            validation {
                // Except for return type, validation is identical to that of
                // CreateStoryCommand. With more fields on the story, likely we
                // may not want to allow updating every field set during
                // creation. At that point, validations will differ.
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

        type UpdateStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: UpdateStoryCommand)
            : TaskResult<Guid, UpdateStoryError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let now = clock.CurrentUtc()
                    let story, event = StoryAggregate.update story cmd.Title cmd.Description now
                    do! stories.ApplyEventAsync ct event
                    return StoryId.value story.Aggregate.Id
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

        type DeleteStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: DeleteStoryCommand)
            : TaskResult<Guid, DeleteStoryError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let event = StoryAggregate.delete story
                    do! stories.ApplyEventAsync ct event
                    return StoryId.value story.Aggregate.Id
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

        type AddTaskToStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | DuplicateTask of Guid

        let fromDomainError =
            function
            | StoryAggregate.AddTaskToStoryError.DuplicateTask id -> DuplicateTask(TaskId.value id)

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: AddTaskToStoryCommand)
            : TaskResult<Guid, AddTaskToStoryError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
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
            // Except for return type, identical to AddTaskToStoryCommand's
            // validate command. With more fields on the task, the two are more
            // likely to differ.
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

        type UpdateTaskError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.UpdateTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (clock: ISystemClock)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: UpdateTaskCommand)
            : TaskResult<Guid, UpdateTaskError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let now = clock.CurrentUtc()
                    let! _, event =
                        updateTask story cmd.TaskId cmd.Title cmd.Description now
                        |> Result.mapError fromDomainError
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

        type DeleteTaskError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.DeleteTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (cmd: DeleteTaskCommand)
            : TaskResult<Guid, DeleteTaskError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! _, event = deleteTask story cmd.TaskId |> Result.mapError fromDomainError
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

        type TaskDto =
            { Id: Guid
              Title: string
              Description: string
              CreatedAt: DateTime
              UpdatedAt: DateTime option }

        module TaskDto =
            let from (task: Task) : TaskDto =
                { Id = Guid.NewGuid()
                  Title = task.Title |> TaskTitle.value
                  Description =
                    match task.Description with
                    | Some d -> d |> TaskDescription.value
                    | None -> null
                  CreatedAt = task.Entity.CreatedAt
                  UpdatedAt = task.Entity.UpdatedAt }

        type StoryDto =
            { Id: Guid
              Title: string
              Description: string
              CreatedAt: DateTime
              UpdatedAt: DateTime option
              Tasks: TaskDto list }

        module StoryDto =
            let from (story: Story) : StoryDto =
                { Id = story.Aggregate.Id |> StoryId.value
                  Title = story.Title |> StoryTitle.value
                  Description =
                    // TODO: doesn't None end up being null with JSON
                    // serialization anyway?
                    story.Description
                    |> Option.map StoryDescription.value
                    |> Option.defaultValue null
                  CreatedAt = story.Aggregate.CreatedAt
                  UpdatedAt = story.Aggregate.UpdatedAt
                  Tasks = story.Tasks |> List.map TaskDto.from }

        type GetStoryByIdError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (identity: IUserIdentity)
            (stories: IStoryRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (qry: GetStoryByIdQuery)
            : TaskResult<StoryDto, GetStoryByIdError> =
            let aux () =
                taskResult {
                    do! isInRole identity Member |> Result.mapError AuthorizationError
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! story =
                        stories.GetByIdAsync ct qry.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value qry.Id))
                    return StoryDto.from story
                }

            runWithDecoratorAsync logger (nameof GetStoryByIdQuery) qry aux

module DomainEventRequest =
    type GetByAggregateIdQuery = { Id: Guid }

    module GetByAggregateIdQuery =
        type GetByAggregateIdValidatedQuery = { Id: Guid }

        let validate (q: GetByAggregateIdQuery) : Validation<GetByAggregateIdValidatedQuery, ValidationError> =
            validation {
                let validatedId = if q.Id = Guid.Empty then Error "Should be non-empty" else Ok q.Id
                let! id = validatedId |> ValidationError.mapError (nameof q.Id)
                return { Id = id }
            }

        type PersistedDomainEventDto =
            { Id: Guid
              AggregateId: Guid
              EventType: string
              EventPayload: string
              CreatedAt: DateTime }

        module PersistedDomainEventDto =
            let from (event: PersistedDomainEvent) : PersistedDomainEventDto =
                { Id = event.Id
                  AggregateId = event.AggregateId
                  EventType = event.EventType
                  EventPayload = event.EventPayload
                  CreatedAt = event.CreatedAt }

        type GetStoryEventsByIdError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list

        let runAsync
            (identity: IUserIdentity)
            (events: IDomainEventRepository)
            (logger: ILogger)
            (ct: CancellationToken)
            (qry: GetByAggregateIdQuery)
            : TaskResult<PersistedDomainEventDto list, GetStoryEventsByIdError> =
            let aux () =
                taskResult {
                    do! isInRole identity Admin |> Result.mapError AuthorizationError
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! events = events.GetByAggregateIdAsync ct qry.Id
                    return events |> List.map PersistedDomainEventDto.from
                }

            runWithDecoratorAsync logger (nameof GetByAggregateIdQuery) qry aux

module ApplicationService =
    // Logic shared across requests.
    ()
