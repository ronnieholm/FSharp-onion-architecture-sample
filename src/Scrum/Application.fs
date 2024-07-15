module Scrum.Application

module Seedwork =
    open System
    open System.Diagnostics
    open FsToolkit.ErrorHandling

    // Contrary to outer layer's Seedwork, core doesn't define a boundary
    // exception. Core communicates errors as values.

    [<Measure>]
    type ms

    type ValidationError = { Field: string; Message: string }

    module ValidationError =
        let create field message = { Field = field; Message = message }
        let mapError field = Result.mapError (create field)

    // A pseudo-aggregate or an aggregate in the application layer. In
    // principle, we could define value types similar to those making up
    // aggregates in the domain, but for this case it's overkill. Prefixed with
    // "Persisted" to avoid confusion with domain's DomainEvent.
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

        override x.ToString() =
            match x with
            | Member -> "member"
            | Admin -> "admin"

    type ScrumIdentity =
        | Anonymous
        | Authenticated of UserId: string * Roles: ScrumRole list

    let isInRole identity role =
        match identity with
        | Anonymous -> false
        | Authenticated(_, roles) ->
            if List.contains role roles then true else false

    type LogMessage =
        // Application specific logging.
        | Request of ScrumIdentity * useCase: string * request: obj
        | RequestDuration of useCase: string * duration: uint<ms>
        | Exception of exn
        // Delegate to .NET ILogger.
        | Err of string
        | Inf of string
        | Dbg of string

    let time (fn: unit -> 't) : 't * uint<ms> =
        let sw = Stopwatch()
        sw.Start()
        let result = fn ()
        let elapsed = (uint sw.ElapsedMilliseconds) * 1u<ms>
        result, elapsed

    // TODO: Write separate tests for the decorator.
    let runWithDecoratorAsync<'TResponse> (log: LogMessage -> unit) identity useCase (fn: unit -> System.Threading.Tasks.Task<'TResponse>) =
        try
            task {
                let useCase = useCase.GetType().Name
                let result, elapsed =
                    time (fun _ ->
                        // TODO: Write test with fn that waits for x ms to make
                        // sure elapsed is correct.
                        log (Request(identity, useCase, useCase))
                        fn ())
                let! result = result
                // Don't log errors from evaluating fn as these are expected
                // errors. We don't want those to pollute the log with.
                log (RequestDuration(useCase, elapsed))
                return result
            }
        with e ->
            log (Exception(e))
            reraise ()

open Seedwork

module Models =
    // Per Zalando API guidelines:
    // https://opensource.zalando.com/restful-api-guidelines/#137)
    type PagedDto<'t> = { Cursor: string option; Items: 't list }

module StoryRequest =
    open System
    open FsToolkit.ErrorHandling
    open Scrum.Domain
    open Scrum.Domain.Shared.Paging
    open Scrum.Domain.StoryAggregate
    open Scrum.Domain.StoryAggregate.TaskEntity
    open Models

    type ApplyEvent = DateTime -> StoryDomainEvent -> System.Threading.Tasks.Task<unit>
    type GetPaged = Limit -> Cursor option -> System.Threading.Tasks.Task<Paged<Story>>

    type CaptureBasicStoryDetailsCommand = { Id: Guid; Title: string; Description: string option }

    module CaptureBasicStoryDetailsCommand =
        type CaptureBasicStoryDetailsValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: CaptureBasicStoryDetailsCommand) =
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
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | DuplicateStory of Guid

        let runAsync utcNow storyExist (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                do!
                    storyExist cmd.Id
                    |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                let story, event =
                    StoryAggregate.captureBasicStoryDetails cmd.Id cmd.Title cmd.Description (utcNow ())
                do! storyApplyEvent (utcNow ()) event
                // Example of publishing the StoryBasicDetailsCaptured domain
                // event to another aggregate:
                // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies ct event
                // Integration events may be generated here and persisted.
                return StoryId.value story.Aggregate.Id
            }

    type ReviseBasicStoryDetailsCommand = { Id: Guid; Title: string; Description: string option }

    module ReviseBasicStoryDetailsCommand =
        type ReviseBasicStoryDetailsValidatedCommand = { Id: StoryId; Title: StoryTitle; Description: StoryDescription option }

        let validate (c: ReviseBasicStoryDetailsCommand) =
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
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync utcNow getStoryById (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    getStoryById cmd.Id
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                let story, event =
                    reviseBasicStoryDetails story cmd.Title cmd.Description (utcNow ())
                do! storyApplyEvent (utcNow ()) event
                return StoryId.value story.Aggregate.Id
            }

    type RemoveStoryCommand = { Id: Guid }

    module RemoveStoryCommand =
        type RemoveStoryValidatedCommand = { Id: StoryId }

        let validate (c: RemoveStoryCommand) : Validation<RemoveStoryValidatedCommand, ValidationError> =
            validation {
                let! id = StoryId.create c.Id |> ValidationError.mapError (nameof c.Id)
                return { Id = id }
            }

        type RemoveStoryError =
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync utcNow getStoryById (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    getStoryById cmd.Id
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                let event = StoryAggregate.removeStory story (utcNow ())
                do! storyApplyEvent (utcNow ()) event
                return StoryId.value story.Aggregate.Id
            }

    type AddBasicTaskDetailsToStoryCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module AddBasicTaskDetailsToStoryCommand =
        type AddBasicTaskDetailsToStoryValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: AddBasicTaskDetailsToStoryCommand) =
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
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | DuplicateTask of Guid

        let fromDomainError =
            function
            | StoryAggregate.AddBasicTaskDetailsToStoryError.DuplicateTask id -> DuplicateTask(TaskId.value id)

        let runAsync utcNow getStoryById (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    getStoryById cmd.StoryId
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                let! _, event =
                    addBasicTaskDetailsToStory story cmd.TaskId cmd.Title cmd.Description (utcNow ())
                    |> Result.mapError fromDomainError
                do! storyApplyEvent (utcNow ()) event
                return TaskId.value cmd.TaskId
            }

    type ReviseBasicTaskDetailsCommand = { StoryId: Guid; TaskId: Guid; Title: string; Description: string option }

    module ReviseBasicTaskDetailsCommand =
        type ReviseBasicTaskDetailsValidatedCommand =
            { StoryId: StoryId
              TaskId: TaskId
              Title: TaskTitle
              Description: TaskDescription option }

        let validate (c: ReviseBasicTaskDetailsCommand) =
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
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.ReviseBasicTaskDetailsError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync utcNow getStoryById (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    getStoryById cmd.StoryId
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                let! _, event =
                    reviseBasicTaskDetails story cmd.TaskId cmd.Title cmd.Description (utcNow ())
                    |> Result.mapError fromDomainError
                do! storyApplyEvent (utcNow ()) event
                return TaskId.value cmd.TaskId
            }

    type RemoveTaskCommand = { StoryId: Guid; TaskId: Guid }

    module RemoveTaskCommand =
        type RemoveTaskValidatedCommand = { StoryId: StoryId; TaskId: TaskId }

        let validate (c: RemoveTaskCommand) =
            validation {
                let! storyId = StoryId.create c.StoryId |> ValidationError.mapError (nameof c.StoryId)
                and! taskId = TaskId.create c.TaskId |> ValidationError.mapError (nameof c.TaskId)
                return { StoryId = storyId; TaskId = taskId }
            }

        type RemoveTaskError =
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid
            | TaskNotFound of Guid

        let fromDomainError =
            function
            | StoryAggregate.RemoveTaskError.TaskNotFound id -> TaskNotFound(TaskId.value id)

        let runAsync utcNow getStoryById (storyApplyEvent: ApplyEvent) identity cmd =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! cmd = validate cmd |> Result.mapError ValidationErrors
                let! story =
                    getStoryById cmd.StoryId
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                let! _, event = removeTask story cmd.TaskId (utcNow ()) |> Result.mapError fromDomainError
                do! storyApplyEvent (utcNow ()) event
                return TaskId.value cmd.TaskId
            }

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

        let validate (q: GetStoryByIdQuery) =
            validation {
                let! storyId = StoryId.create q.Id |> ValidationError.mapError (nameof q.Id)
                return { Id = storyId }
            }

        type GetStoryByIdError =
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list
            | StoryNotFound of Guid

        let runAsync getStoryById identity qry =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! qry = validate qry |> Result.mapError ValidationErrors
                let! story =
                    getStoryById qry.Id
                    |> TaskResult.requireSome (StoryNotFound(StoryId.value qry.Id))
                return StoryDto.from story
            }

    // Query included to illustrate paging. In practice, we wouldn't query every
    // story. Instead, queries would be for stories in a product backlog, a
    // release backlog, or a sprint backlog, but we don't support organizing
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

        let validate (q: GetStoriesPagedQuery) =
            validation {
                let! limit = Limit.create q.Limit |> ValidationError.mapError (nameof q.Limit)
                and! cursor =
                    match q.Cursor with
                    | Some c -> Cursor.create c |> ValidationError.mapError (nameof q.Cursor) |> Result.map Some
                    | None -> Ok None
                return { Limit = limit; Cursor = cursor }
            }

        type GetStoriesPagedError =
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list

        let runAsync (getStoriesPaged: GetPaged) identity qry =
            taskResult {
                do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                let! qry = validate qry |> Result.mapError ValidationErrors
                let! storiesPage = getStoriesPaged qry.Limit qry.Cursor
                return
                    // Per Zalando guidelines, we could write a JsonConverter to
                    // replace "Items" by "Stories".
                    { PagedDto.Cursor = storiesPage.Cursor |> Option.map Cursor.value
                      Items = storiesPage.Items |> List.map StoryDto.from }
            }

module DomainEventRequest =
    open System
    open FsToolkit.ErrorHandling
    open Scrum.Domain
    open Scrum.Domain.Shared.Paging
    open Models

    type GetByAggregateId = Guid -> Limit -> Cursor option -> System.Threading.Tasks.Task<Paged<PersistedDomainEvent>>

    type GetByAggregateIdQuery = { Id: Guid; Limit: int; Cursor: string option }

    module GetByAggregateIdQuery =
        type GetByAggregateIdValidatedQuery = { Id: Guid; Limit: Limit; Cursor: Cursor option }

        let validate (q: GetByAggregateIdQuery) =
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
            let from (event: PersistedDomainEvent) =
                { Id = event.Id
                  AggregateId = event.AggregateId
                  AggregateType = event.AggregateType
                  EventType = event.EventType
                  EventPayload = event.EventPayload
                  CreatedAt = event.CreatedAt }

        type GetStoryEventsByIdError =
            | AuthorizationError of ScrumRole
            | ValidationErrors of ValidationError list

        let runAsync (getByAggregateId: GetByAggregateId) identity qry =
            taskResult {
                do! isInRole identity Admin |> Result.requireTrue (AuthorizationError Admin)
                let! qry = validate qry |> Result.mapError ValidationErrors
                let! eventsPage = getByAggregateId qry.Id qry.Limit qry.Cursor
                return
                    { PagedDto.Cursor = eventsPage.Cursor |> Option.map Cursor.value
                      Items = eventsPage.Items |> List.map PersistedDomainEventDto.from }
            }

module Service =
    // Services shared across requests.
    ()
