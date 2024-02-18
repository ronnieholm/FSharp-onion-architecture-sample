module Scrum.Application

module Seedwork =
    open System
    open System.Diagnostics
    open System.Threading
    open FsToolkit.ErrorHandling
    open Scrum.Domain.Shared.Paging
    open Scrum.Domain.StoryAggregate

    // Contrary to outer layer's Seedwork, core doesn't define a boundary
    // exception. Core communicates errors as values.

    [<Measure>]
    type ms

    type ValidationError = { Field: string; Message: string }

    module ValidationError =
        let create (field: string) (message: string) = { Field = field; Message = message }
        let mapError (field: string) : (Result<'a, string> -> Result<'a, ValidationError>) = Result.mapError (create field)

    // A pseudo-aggregate or an aggregate in the application layer. In
    // principle, we could define value types similar to those making up
    // aggregates in the domain, but for this case it's overkill. Prefixed
    // with "Persisted" to avoid confusion with domain's DomainEvent.
    type PersistedDomainEvent =
        { Id: Guid
          AggregateId: Guid
          AggregateType: string
          EventType: string
          EventPayload: string
          CreatedAt: DateTime }

    // Roles a user may possess in the application, not in Scrum as a process.
    // We're in the application layer, not domain layer, after all.
    type ScrumRole =
        | Member
        | Admin

        override x.ToString() : string =
            match x with
            | Member -> "member"
            | Admin -> "admin"

    type ScrumIdentity =
        | Anonymous
        | Authenticated of UserId: string * Role: ScrumRole list

    [<Interface>]
    // Could be called IIdentity but that interface is part of .NET.
    type IScrumIdentity =
        abstract GetCurrent: unit -> ScrumIdentity

    // The factory interfaces are for groping inside AppEnv. Without factory
    // interfaces, AppEnv would implement interfaces at the same same level,
    // e.g., AppEnv.GetCurrent. A factory interfaces turns this into
    // AppEnv.UserIdentity.GetCurrent, and means we can inject the dependency as
    // a whole into another type by passing into that type AppEnv.UserIdentity.
    [<Interface>]
    type IScrumIdentityFactory =
        abstract Identity: IScrumIdentity

    [<Interface>]
    type IClock =
        abstract CurrentUtc: unit -> DateTime

    [<Interface>]
    type IClockFactory =
        abstract Clock: IClock

    [<Interface>]
    // Could be ILogger but that interface is part of .NET.
    type IScrumLogger =
        // Application specific logging.
        abstract LogRequest: ScrumIdentity -> string -> obj -> unit
        abstract LogRequestDuration: string -> uint<ms> -> unit
        abstract LogException: exn -> unit

        // Delegates to .NET's ILogger.
        abstract LogError: string -> unit
        abstract LogInformation: string -> unit
        abstract LogDebug: string -> unit

    [<Interface>]
    type IScrumLoggerFactory =
        abstract Logger: IScrumLogger

    [<Interface>]
    type IStoryRepositoryFactory =
        abstract Stories: IStoryRepository

    [<Interface>]
    // Mirroring the PersistedDomainEvent type, this repository is defined in
    // the application, not domain, layer. While domain events are part of the
    // domain, persisted domain events aren't. After an event is processed, it's
    // no longer needed. We persist domain events for troubleshooting only.
    type IDomainEventRepository =
        abstract GetByAggregateIdAsync:
            CancellationToken -> Guid -> Limit -> Cursor option -> System.Threading.Tasks.Task<Paged<PersistedDomainEvent>>

    [<Interface>]
    type IDomainEventRepositoryFactory =
        abstract DomainEvents: IDomainEventRepository

    [<Interface>]
    // App in application environment refers to the application layer, not the
    // application as a whole. Layers outside application still use the .NET
    // dependency injection container.
    type IAppEnv =
        inherit IScrumIdentityFactory
        inherit IScrumLoggerFactory
        inherit IClockFactory
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

    let runWithDecoratorAsync (env: IAppEnv) (useCase: string) (cmd: 't) (fn: unit -> TaskResult<'a, 'b>) : TaskResult<'a, 'b> =
        let result, elapsed =
            time (fun _ ->
                env.Logger.LogRequest (env.Identity.GetCurrent()) useCase cmd
                taskResult { return! fn () })
        // Don't log errors from evaluating fn as these are expected errors. We
        // don't want those to pollute the log with.
        env.Logger.LogRequestDuration useCase elapsed
        result

    let isInRole (identity: IScrumIdentity) (role: ScrumRole) : Result<unit, string> =
        match identity.GetCurrent() with
        | Anonymous -> Error("Anonymous user unsupported")
        | Authenticated(_, roles) -> if List.contains role roles then Ok() else Error($"Missing role '{role}'")

open Seedwork

module SharedModels =
    // Per Zalando API guidelines:
    // https://opensource.zalando.com/restful-api-guidelines/#137)
    type PagedDto<'t> = { Cursor: string option; Items: 't list }

module StoryAggregateRequest =
    open System
    open System.Threading
    open FsToolkit.ErrorHandling
    open Scrum.Domain
    open Scrum.Domain.Shared.Paging
    open Scrum.Domain.StoryAggregate
    open Scrum.Domain.StoryAggregate.TaskEntity
    open SharedModels

    type CaptureBasicStoryDetailsCommand = { Id: Guid; Title: string; Description: string option }

    module CaptureBasicStoryDetailsCommand =
        type CaptureBasicStoryDetailsValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: CaptureBasicStoryDetailsCommand) : Validation<CaptureBasicStoryDetailsValidatedCommand, ValidationError> =
            validation {
                let! id = StoryId.create c.Id |> ValidationError.mapError (nameof c.Id)
                and! title = StoryTitle.create c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        StoryDescription.create d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { Id = id; Title = title; Description = description }
            }

        type CaptureBasicStoryDetailsError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | DuplicateStory of Guid
            | DuplicateTasks of Guid list

        let fromDomainError =
            function
            | StoryAggregate.CaptureBasicStoryDetailsError.DuplicateTasks ids -> DuplicateTasks(ids |> List.map TaskId.value)

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (cmd: CaptureBasicStoryDetailsCommand)
            : TaskResult<Guid, CaptureBasicStoryDetailsError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    do!
                        env.Stories.ExistAsync ct cmd.Id
                        |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                    let! story, event =
                        StoryAggregate.captureBasicStoryDetails cmd.Id cmd.Title cmd.Description [] (env.Clock.CurrentUtc()) None
                        |> Result.mapError fromDomainError
                    do! env.Stories.ApplyEventAsync ct event
                    // Example of publishing the StoryBasicDetailsCaptured domain event to
                    // another aggregate:
                    // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies ct event
                    // Integration events may be generated here and persisted.
                    return StoryId.value story.Aggregate.Id
                }

            runWithDecoratorAsync env (nameof CaptureBasicStoryDetailsCommand) cmd aux

    type ReviseBasicStoryDetailsCommand = { Id: Guid; Title: string; Description: string option }

    module ReviseBasicStoryDetailsCommand =
        type ReviseBasicStoryDetailsValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: ReviseBasicStoryDetailsCommand) : Validation<ReviseBasicStoryDetailsValidatedCommand, ValidationError> =
            validation {
                // Except for return type, this validation is identical to that
                // of CreateStoryCommand. With more fields on the story, likely
                // we don't want to allow updating every field set during
                // creation. At that point, validations will differ.
                let! id = StoryId.create c.Id |> ValidationError.mapError (nameof c.Id)
                and! title = StoryTitle.create c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        StoryDescription.create d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { Id = id; Title = title; Description = description }
            }

        type ReviseBasicStoryDetailsError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (cmd: ReviseBasicStoryDetailsCommand)
            : TaskResult<Guid, ReviseBasicStoryDetailsError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let story, event =
                        StoryAggregate.reviseBasicStoryDetails story cmd.Title cmd.Description (env.Clock.CurrentUtc())
                    do! env.Stories.ApplyEventAsync ct event
                    return StoryId.value story.Aggregate.Id
                }

            runWithDecoratorAsync env (nameof ReviseBasicStoryDetailsCommand) cmd aux

    type RemoveStoryCommand = { Id: Guid }

    module RemoveStoryCommand =
        type RemoveStoryValidatedCommand = { Id: StoryId }

        let validate (c: RemoveStoryCommand) : Validation<RemoveStoryValidatedCommand, ValidationError> =
            validation {
                let! id = StoryId.create c.Id |> ValidationError.mapError (nameof c.Id)
                return { Id = id }
            }

        type RemoveStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync (env: IAppEnv) (ct: CancellationToken) (cmd: RemoveStoryCommand) : TaskResult<Guid, RemoveStoryError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let event = StoryAggregate.removeStory story (env.Clock.CurrentUtc())
                    do! env.Stories.ApplyEventAsync ct event
                    return StoryId.value story.Aggregate.Id
                }

            runWithDecoratorAsync env (nameof RemoveStoryCommand) cmd aux

    type AddBasicTaskDetailsToStoryCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module AddBasicTaskDetailsToStoryCommand =
        type AddBasicTaskDetailsToStoryValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: AddBasicTaskDetailsToStoryCommand) : Validation<AddBasicTaskDetailsToStoryValidatedCommand, ValidationError> =
            validation {
                let! storyId = StoryId.create c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.create c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                and! title = TaskTitle.create c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        TaskDescription.create d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { StoryId = storyId; TaskId = taskId; Title = title; Description = description }
            }

        type AddBasicTaskDetailsToStoryError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | DuplicateTask of Guid

        let fromDomainError =
            function
            | StoryAggregate.AddBasicTaskDetailsToStoryError.DuplicateTask id -> DuplicateTask(TaskId.value id)

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (cmd: AddBasicTaskDetailsToStoryCommand)
            : TaskResult<Guid, AddBasicTaskDetailsToStoryError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let task = create cmd.TaskId cmd.Title cmd.Description (env.Clock.CurrentUtc()) None
                    let! _, event =
                        addBasicTaskDetailsToStory story task (env.Clock.CurrentUtc())
                        |> Result.mapError fromDomainError
                    do! env.Stories.ApplyEventAsync ct event
                    return TaskId.value task.Entity.Id
                }

            runWithDecoratorAsync env (nameof AddBasicTaskDetailsToStoryCommand) cmd aux

    type ReviseBasicTaskDetailsCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module ReviseBasicTaskDetailsCommand =
        type ReviseBasicTaskDetailsValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: ReviseBasicTaskDetailsCommand) : Validation<ReviseBasicTaskDetailsValidatedCommand, ValidationError> =
            // Except for return type, identical to AddTaskToStoryCommand's
            // validate command. With more fields on the task, the two are
            // likely to differ.
            validation {
                let! storyId = StoryId.create c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.create c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                and! title = TaskTitle.create c.Title |> ValidationError.mapError (nameof c.Title)
                and! description =
                    match c.Description with
                    | Some d ->
                        TaskDescription.create d
                        |> ValidationError.mapError (nameof c.Description)
                        |> Result.map Some
                    | None -> Ok None
                return { StoryId = storyId; TaskId = taskId; Title = title; Description = description }
            }

        type ReviseBasicTaskDetailsError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.ReviseBasicTaskDetailsError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (cmd: ReviseBasicTaskDetailsCommand)
            : TaskResult<Guid, ReviseBasicTaskDetailsError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! _, event =
                        reviseBasicTaskDetails story cmd.TaskId cmd.Title cmd.Description (env.Clock.CurrentUtc())
                        |> Result.mapError fromDomainError
                    do! env.Stories.ApplyEventAsync ct event
                    return TaskId.value cmd.TaskId
                }

            runWithDecoratorAsync env (nameof ReviseBasicTaskDetailsCommand) cmd aux

    type RemoveTaskCommand = { StoryId: Guid; TaskId: Guid }

    module RemoveTaskCommand =
        type RemoveTaskValidatedCommand = { StoryId: StoryId; TaskId: TaskId }

        let validate (c: RemoveTaskCommand) : Validation<RemoveTaskValidatedCommand, ValidationError> =
            validation {
                let! storyId = StoryId.create c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.create c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                return { StoryId = storyId; TaskId = taskId }
            }

        type RemoveTaskError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.RemoveTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync (env: IAppEnv) (ct: CancellationToken) (cmd: RemoveTaskCommand) : TaskResult<Guid, RemoveTaskError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! _, event =
                        removeTask story cmd.TaskId (env.Clock.CurrentUtc())
                        |> Result.mapError fromDomainError
                    do! env.Stories.ApplyEventAsync ct event
                    return TaskId.value cmd.TaskId
                }

            runWithDecoratorAsync env (nameof RemoveTaskCommand) cmd aux

    type GetStoryByIdQuery = { Id: Guid }

    type TaskDto =
        { Id: Guid
          Title: string
          Description: string option
          CreatedAt: DateTime
          UpdatedAt: DateTime option }

    module TaskDto =
        let from (task: Task) : TaskDto =
            { Id = task.Entity.Id |> TaskId.value
              Title = task.Title |> TaskTitle.value
              Description = task.Description |> Option.map TaskDescription.value
              CreatedAt = task.Entity.CreatedAt
              UpdatedAt = task.Entity.UpdatedAt }

    type StoryDto =
        { Id: Guid
          Title: string
          Description: string option
          CreatedAt: DateTime
          UpdatedAt: DateTime option
          Tasks: TaskDto list }

    module StoryDto =
        let from (story: Story) : StoryDto =
            { Id = story.Aggregate.Id |> StoryId.value
              Title = story.Title |> StoryTitle.value
              Description = story.Description |> Option.map StoryDescription.value
              CreatedAt = story.Aggregate.CreatedAt
              UpdatedAt = story.Aggregate.UpdatedAt
              Tasks = story.Tasks |> List.map TaskDto.from }

    module GetStoryByIdQuery =
        type GetStoryByIdValidatedQuery = { Id: StoryId }

        let validate (q: GetStoryByIdQuery) : Validation<GetStoryByIdValidatedQuery, ValidationError> =
            validation {
                let! storyId = StoryId.create q.Id |> ValidationError.mapError (nameof q.Id)
                return { Id = storyId }
            }

        type GetStoryByIdError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync (env: IAppEnv) (ct: CancellationToken) (qry: GetStoryByIdQuery) : TaskResult<StoryDto, GetStoryByIdError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! story =
                        env.Stories.GetByIdAsync ct qry.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value qry.Id))
                    return StoryDto.from story
                }

            runWithDecoratorAsync env (nameof GetStoryByIdQuery) qry aux

    // Query included to illustrate paging. In practice, we wouldn't query
    // every story. Instead, queries would be for stories in a product backlog,
    // a release backlog, or a sprint backlog, but we don't support organizing
    // stories into a backlog. For a backlog, it would likely only contain
    // StoryIds. Then either the client would lookup storyIds one by one or
    // submit a batch request for StoryIds.
    //
    // In the same vain, GetStoryTasksPagedQuery wouldn't make much business
    // sense. Tasks are cheap to include with stories, so best keep number of
    // queries to a minimum.
    type GetStoriesPagedQuery = { Limit: int; Cursor: string option }

    module GetStoriesPagedQuery =
        type GetStoriesPagedValidatedQuery = { Limit: Limit; Cursor: Cursor option }

        let validate (q: GetStoriesPagedQuery) : Validation<GetStoriesPagedValidatedQuery, ValidationError> =
            validation {
                let! limit = Limit.create q.Limit |> ValidationError.mapError (nameof q.Limit)
                and! cursor =
                    match q.Cursor with
                    | Some c -> Cursor.create c |> ValidationError.mapError (nameof q.Cursor) |> Result.map Some
                    | None -> Ok None
                return { Limit = limit; Cursor = cursor }
            }

        type GetStoriesPagedError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (qry: GetStoriesPagedQuery)
            : TaskResult<PagedDto<StoryDto>, GetStoriesPagedError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Member |> Result.mapError AuthorizationError
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! stories = env.Stories.GetStoriesPagedAsync ct qry.Limit qry.Cursor
                    return
                        // Per Zalando guidelines, we could write a
                        // JsonConverter to replace "Items" by "Stories".
                        { PagedDto.Cursor = stories.Cursor |> Option.map Cursor.value
                          Items = stories.Items |> List.map StoryDto.from }
                }

            runWithDecoratorAsync env (nameof GetStoriesPagedQuery) qry aux

module DomainEventRequest =
    open System
    open System.Threading
    open FsToolkit.ErrorHandling
    open Scrum.Domain
    open Scrum.Domain.Shared.Paging
    open SharedModels

    type GetByAggregateIdQuery = { Id: Guid; Limit: int; Cursor: string option }

    module GetByAggregateIdQuery =
        type GetByAggregateIdValidatedQuery = { Id: Guid; Limit: Limit; Cursor: Cursor option }

        let validate (q: GetByAggregateIdQuery) : Validation<GetByAggregateIdValidatedQuery, ValidationError> =
            validation {
                let! id = Validation.Guid.notEmpty q.Id |> ValidationError.mapError (nameof q.Id)
                and! limit = Limit.create q.Limit |> ValidationError.mapError (nameof q.Limit)
                and! cursor =
                    match q.Cursor with
                    | Some c -> Cursor.create c |> ValidationError.mapError (nameof q.Cursor) |> Result.map Some
                    | None -> Ok None
                return { Id = id; Limit = limit; Cursor = cursor }
            }

        type PersistedDomainEventDto =
            { Id: Guid
              AggregateId: Guid
              AggregateType: string
              EventType: string
              EventPayload: string
              CreatedAt: DateTime }

        module PersistedDomainEventDto =
            let from (event: PersistedDomainEvent) : PersistedDomainEventDto =
                { Id = event.Id
                  AggregateId = event.AggregateId
                  AggregateType = event.AggregateType
                  EventType = event.EventType
                  EventPayload = event.EventPayload
                  CreatedAt = event.CreatedAt }

        type GetStoryEventsByIdError =
            | AuthorizationError of string
            | ValidationErrors of ValidationError list

        let runAsync
            (env: IAppEnv)
            (ct: CancellationToken)
            (qry: GetByAggregateIdQuery)
            : TaskResult<PagedDto<PersistedDomainEventDto>, GetStoryEventsByIdError> =
            let aux () =
                taskResult {
                    do! isInRole env.Identity Admin |> Result.mapError AuthorizationError
                    let! qry = validate qry |> Result.mapError ValidationErrors
                    let! events = env.DomainEvents.GetByAggregateIdAsync ct qry.Id qry.Limit qry.Cursor
                    return
                        { PagedDto.Cursor = events.Cursor |> Option.map Cursor.value
                          Items = events.Items |> List.map PersistedDomainEventDto.from }
                }

            runWithDecoratorAsync env (nameof GetByAggregateIdQuery) qry aux

module ApplicationService =
    // Services shared across requests.
    ()
