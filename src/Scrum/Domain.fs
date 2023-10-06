module Scrum.Domain

open System
open System.Threading
open System.Threading.Tasks

module Seedwork =
    // Contrary to other layer's Seedwork, Domain doesn't define a boundary
    // exception. Domain communicates errors as values. 
    
    type Entity<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
    type AggregateRoot<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }

module Shared =
    // Value objects and entities shared across aggregates.
    ()

module StoryAggregate =
    open Seedwork

    module TaskEntity =
        type TaskId = TaskId of Guid

        module TaskId =
            let validate =
                function
                | v when v = Guid.Empty -> Error "Should be non-empty"
                | v -> Ok(TaskId v)

            let value (TaskId id) = id

        type TaskTitle = TaskTitle of string

        module TaskTitle =
            let maxLength = 100

            let validate =
                function
                | v when String.IsNullOrWhiteSpace v -> Error "Should be non-null, non-empty or non-whitespace"
                | v when v.Length > maxLength -> Error $"Should contain less than or equal to {maxLength} characters"
                | v -> Ok(TaskTitle v)

            let value (TaskTitle id) = id

        type TaskDescription = TaskDescription of string

        module TaskDescription =
            let maxLength = 1000

            let validate =
                function
                | v when String.IsNullOrWhiteSpace v -> Error "Should be non-null, non-empty or non-whitespace"
                | v when v.Length > maxLength -> Error $"Should contain less than or equal to ${maxLength} characters"
                | v -> Ok(TaskDescription v)

            let value (TaskDescription id) = id

        [<NoComparison; NoEquality>]
        type Task =
            { Entity: Entity<TaskId>
              Title: TaskTitle
              Description: TaskDescription option }

        let create (id: TaskId) (title: TaskTitle) (description: TaskDescription option) (createdAt: DateTime) : Task =
            { Entity = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
              Title = title
              Description = description }

        let equals a b = a.Entity.Id = b.Entity.Id

    type StoryId = StoryId of Guid

    module StoryId =
        let validate =
            function
            | v when v = Guid.Empty -> Error "Should be non-empty"
            | v -> Ok(StoryId v)

        let value (StoryId id) = id

    type StoryTitle = StoryTitle of string

    module StoryTitle =
        let maxLength = 100

        let create =
            function
            | v when String.IsNullOrWhiteSpace v -> Error "Should be non-null, non-empty or non-whitespace"
            | v when v.Length > maxLength -> Error $"Should contain less than or equal to {maxLength} characters"
            | v -> Ok(StoryTitle v)

        let value (StoryTitle id) = id

    type StoryDescription = StoryDescription of string

    module StoryDescription =
        let maxLength = 1000

        let validate =
            function
            | v when String.IsNullOrWhiteSpace v -> Error "Should be non-null, non-empty or non-whitespace"
            | v when v.Length > maxLength -> Error $"Should contain less than or equal to {maxLength} characters"
            | v -> Ok(StoryDescription v)

        let value (StoryDescription id) = id

    [<NoComparison; NoEquality>]
    type Story =
        { Aggregate: AggregateRoot<StoryId>
          Title: StoryTitle
          Description: StoryDescription option
          Tasks: TaskEntity.Task list }

    type StoryCreated =
        { StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option
          OccurredAt: DateTime }

    type StoryUpdated =
        { StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option
          OccurredAt: DateTime }

    type StoryDeleted = { StoryId: StoryId }

    type TaskAddedToStory =
        { StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option
          OccurredAt: DateTime }

    type TaskUpdated =
        { StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option
          OccurredAt: DateTime }

    type TaskDeleted = { StoryId: StoryId; TaskId: TaskEntity.TaskId }

    type DomainEvent =
        | StoryCreated of StoryCreated
        | StoryUpdated of StoryUpdated
        | StoryDeleted of StoryDeleted
        | TaskAddedToStory of TaskAddedToStory
        | TaskUpdated of TaskUpdated
        | TaskDeleted of TaskDeleted

    let create (id: StoryId) (title: StoryTitle) (description: StoryDescription option) (createdAt: DateTime) : Story * DomainEvent =
        { Aggregate = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
          Title = title
          Description = description
          Tasks = [] },
        DomainEvent.StoryCreated(
            { StoryId = id
              StoryTitle = title
              StoryDescription = description
              OccurredAt = createdAt }
        )

    let update (story: Story) (title: StoryTitle) (description: StoryDescription option) (updatedAt: DateTime) : Story * DomainEvent =
        let root = { story.Aggregate with UpdatedAt = Some updatedAt }
        let story =
            { story with Aggregate = root; Title = title; Description = description }
        let event =
            DomainEvent.StoryUpdated(
                { StoryId = story.Aggregate.Id
                  StoryTitle = title
                  StoryDescription = description
                  OccurredAt = updatedAt }
            )
        story, event

    let delete (story: Story) : DomainEvent =
        // Depending on the specifics of a domain, we might want to explicitly
        // delete the story's tasks and emit task deleted events. In this case,
        // we leave cascade delete to the store.
        DomainEvent.StoryDeleted({ StoryId = story.Aggregate.Id })

    open TaskEntity

    type AddTaskToStoryError = DuplicateTask of TaskId

    let addTaskToStory (story: Story) (task: Task) (createdAt: DateTime) : Result<Story * DomainEvent, AddTaskToStoryError> =
        let duplicate = story.Tasks |> List.exists (equals task)

        if duplicate then
            Error(DuplicateTask task.Entity.Id)
        else
            Ok(
                { story with Tasks = task :: story.Tasks },
                DomainEvent.TaskAddedToStory(
                    { StoryId = story.Aggregate.Id
                      TaskId = task.Entity.Id
                      TaskTitle = task.Title
                      TaskDescription = task.Description
                      OccurredAt = createdAt }
                )
            )

    type UpdateTaskError = TaskNotFound of TaskId

    let updateTask
        (story: Story)
        (taskId: TaskId)
        (title: TaskTitle)
        (description: TaskDescription option)
        (updatedAt: DateTime)
        : Result<Story * DomainEvent, UpdateTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let task = story.Tasks[idx]
            let tasks = story.Tasks |> List.removeAt idx
            let entity = { task.Entity with UpdatedAt = Some updatedAt }
            let updatedTask =
                { task with Entity = entity; Title = title; Description = description }
            let story = { story with Tasks = updatedTask :: tasks }
            let event =
                DomainEvent.TaskUpdated(
                    { StoryId = story.Aggregate.Id
                      TaskId = taskId
                      TaskTitle = title
                      TaskDescription = description
                      OccurredAt = updatedAt }
                )
            Ok(story, event)
        | None -> Error(TaskNotFound taskId)

    type DeleteTaskError = TaskNotFound of TaskId

    let deleteTask (story: Story) (taskId: TaskId) : Result<Story * DomainEvent, DeleteTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let tasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = tasks }
            let event =
                DomainEvent.TaskDeleted({ StoryId = story.Aggregate.Id; TaskId = taskId })
            Ok(story, event)
        | None -> Error(TaskNotFound taskId)

    [<Interface>]
    type IStoryRepository =
        abstract ExistAsync: CancellationToken -> StoryId -> Task<bool>
        abstract GetByIdAsync: CancellationToken -> StoryId -> Task<Story option>
        abstract ApplyEventAsync: CancellationToken -> DomainEvent -> Task<unit>

module DomainService =
    // Services shared across aggregates.
    ()