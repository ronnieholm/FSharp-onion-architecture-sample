module Scrum.Domain

open System
open System.Threading
open System.Threading.Tasks

module Seedwork =
    // Strictly speaking, an AggregateRoot is an Entity. If the root holds at
    // least the same information as an entity, th relationship could be
    // modeled as:
    // type AggregateRoot<'id> = { Entity: Entity<'id> }
    // However, current F# makes updating this model cumbersome. In the next
    // version of F#, released Nov 14, 2023, the "Nested record field
    // copy-and-update" makes it less cumbersome.
    type Entity<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
    type AggregateRoot<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }

    type DomainEvent = { OccurredAt: DateTime }

// Constraint validation on primitive types for reuse across value object
// creations.
module Validation =
    module Guid =
        let notEmpty (v: Guid) : Result<Guid, string> = if v = Guid.Empty then Error "Should be non-empty" else Ok v

    module String =
        let notNullOrWhitespace (v: string) : Result<string, string> =
            if String.IsNullOrWhiteSpace(v) then
                Error "Should be non-null, non-empty or non-whitespace"
            else
                Ok v

        let maxLength (l: int) (v: string) : Result<string, string> =
            if v.Length > l then
                Error $"Should contain less than or equal to {l} characters"
            else
                Ok v

open Validation

module Shared =
    // Value objects and entities shared across aggregates.
    ()

module StoryAggregate =
    open Seedwork

    module TaskEntity =
        type TaskId = private TaskId of Guid

        module TaskId =
            let create (g: Guid) : Result<TaskId, string> = g |> Guid.notEmpty |> Result.map TaskId

            let value (TaskId id) = id

        type TaskTitle = private TaskTitle of string

        module TaskTitle =
            // If we defined operators >=> as Result.bind and <!> as
            // Result.map, a short, point-free form would become:
            // let create = String.notNullOrWhitespace >=> String.maxLength 100 <!> TaskTitle
            // While short, it's harder to understand.
            let create (v: string) : Result<TaskTitle, string> =
                v
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength 100)
                |> Result.map TaskTitle

            let value (TaskTitle id) = id

        type TaskDescription = private TaskDescription of string

        module TaskDescription =
            let create (v: string) : Result<TaskDescription, string> =
                v
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength 1000)
                |> Result.map TaskDescription

            let value (TaskDescription id) = id

        [<NoComparison; NoEquality>]
        type Task =
            { Entity: Entity<TaskId>
              Title: TaskTitle
              Description: TaskDescription option }

        let create
            (id: TaskId)
            (title: TaskTitle)
            (description: TaskDescription option)
            (createdAt: DateTime)
            (updatedAt: DateTime option)
            : Task =
            { Entity = { Id = id; CreatedAt = createdAt; UpdatedAt = updatedAt }
              Title = title
              Description = description }

        let equals a b = a.Entity.Id = b.Entity.Id

    type StoryId = private StoryId of Guid

    module StoryId =
        let create (v: Guid) : Result<StoryId, string> = v |> Guid.notEmpty |> Result.map StoryId

        let value (StoryId id) = id

    type StoryTitle = StoryTitle of string

    module StoryTitle =
        let create (v: string) : Result<StoryTitle, string> =
            v
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 100)
            |> Result.map StoryTitle

        let value (StoryTitle id) = id

    type StoryDescription = StoryDescription of string

    module StoryDescription =
        let create (v: string) : Result<StoryDescription, string> =
            v
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 1000)
            |> Result.map StoryDescription

        let value (StoryDescription id) = id

    [<NoComparison; NoEquality>]
    type Story =
        { Aggregate: AggregateRoot<StoryId>
          Title: StoryTitle
          Description: StoryDescription option
          Tasks: TaskEntity.Task list }

    type StoryCreated =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type StoryUpdated =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type StoryDeleted = { StoryId: StoryId; OccurredAt: DateTime }

    type TaskAddedToStory =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type TaskUpdated =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type TaskDeleted = { DomainEvent: DomainEvent; StoryId: StoryId; TaskId: TaskEntity.TaskId }

    type StoryDomainEvent =
        | StoryCreated of StoryCreated
        | StoryUpdated of StoryUpdated
        | StoryDeleted of StoryDeleted
        | TaskAddedToStory of TaskAddedToStory
        | TaskUpdated of TaskUpdated
        | TaskDeleted of TaskDeleted

    open TaskEntity

    let create
        (id: StoryId)
        (title: StoryTitle)
        (description: StoryDescription option)
        (tasks: Task list)
        (createdAt: DateTime)
        (updatedAt: DateTime option)
        : Story =
        // TODO: Should return result as we should validate task Ids are unique.
        { Aggregate = { Id = id; CreatedAt = createdAt; UpdatedAt = updatedAt }
          Title = title
          Description = description
          Tasks = tasks }

    let update (story: Story) (title: StoryTitle) (description: StoryDescription option) (updatedAt: DateTime) : Story =
        let root = { story.Aggregate with UpdatedAt = Some updatedAt }
        { story with Aggregate = root; Title = title; Description = description }

    let delete (_: Story) : unit =
        // Depending on the specifics of a domain, we might want to explicitly
        // delete the story's tasks and emit task deleted events. In this case,
        // we leave cascade delete to the store.
        ()

    type AddTaskToStoryError = DuplicateTask of TaskId

    let addTaskToStory (story: Story) (task: Task) : Result<Story, AddTaskToStoryError> =
        let duplicate = story.Tasks |> List.exists (equals task)
        if duplicate then
            Error(DuplicateTask task.Entity.Id)
        else
            Ok { story with Tasks = task :: story.Tasks }

    type UpdateTaskError = TaskNotFound of TaskId

    let updateTask
        (story: Story)
        (taskId: TaskId)
        (title: TaskTitle)
        (description: TaskDescription option)
        (updatedAt: DateTime)
        : Result<Story, UpdateTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let task = story.Tasks[idx]
            let tasks = story.Tasks |> List.removeAt idx
            let entity = { task.Entity with UpdatedAt = Some updatedAt }
            let updatedTask =
                { task with Entity = entity; Title = title; Description = description }
            let story = { story with Tasks = updatedTask :: tasks }
            Ok story
        | None -> Error(TaskNotFound taskId)

    type DeleteTaskError = TaskNotFound of TaskId

    let deleteTask (story: Story) (taskId: TaskId) : Result<Story, DeleteTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let tasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = tasks }
            Ok story
        | None -> Error(TaskNotFound taskId)

    [<Interface>]
    type IStoryRepository =
        abstract ExistAsync: CancellationToken -> StoryId -> Task<bool>
        abstract GetByIdAsync: CancellationToken -> StoryId -> Task<Story option>
        abstract ApplyEventAsync: CancellationToken -> StoryDomainEvent -> Task<unit>

module DomainService =
    // Services shared across aggregates.
    ()
