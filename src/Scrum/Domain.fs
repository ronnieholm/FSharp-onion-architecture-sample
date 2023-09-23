module Scrum.Domain

open System
open System.Threading
open System.Threading.Tasks

module Seedwork =
    type Entity<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
    type AggregateRoot<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }

module SharedValueObjects =
    ()

module StoryAggregate =
    open Seedwork

    module TaskEntity =
        type TaskId = TaskId of Guid
        module TaskId =
            // TODO: Disallow empty Guid
            let validate value = Ok(TaskId value)
            let value (TaskId id) = id

        type TaskTitle = TaskTitle of string
        module TaskTitle =
            let validate value =
                match value with
                | v when String.IsNullOrWhiteSpace v -> Error "Shouldn't by empty or whitespace"
                | v when v.Length > 100 -> Error "Shouldn't contain more than 100 characters"
                | v -> Ok(TaskTitle v)

            let value (TaskTitle id) = id

        type TaskDescription = TaskDescription of string
        module TaskDescription =
            let validate value =
                match value with
                | v when String.IsNullOrWhiteSpace v -> Error "Shouldn't by empty or whitespace"
                | v when v.Length > 1000 -> Error "Shouldn't contain more than 1000 characters"
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
        // TODO: Disallow empty Guid
        let validate value = Ok(StoryId value)
        let value (StoryId id) = id

    type StoryTitle = StoryTitle of string
    module StoryTitle =
        // TODO: disallow newline characters
        // TODO: Add length as constant below module
        let create value =
            match value with
            | v when String.IsNullOrWhiteSpace v -> Error "Shouldn't by empty or whitespace"
            | v when v.Length > 100 -> Error "Shouldn't contain more than 100 characters"
            | v -> Ok(StoryTitle v)

        let value (StoryTitle id) = id

    type StoryDescription = StoryDescription of string
    module StoryDescription =
        let validate value =
            match value with
            | v when String.IsNullOrWhiteSpace v -> Error "Shouldn't by empty or whitespace"
            | v when v.Length > 1000 -> Error "Shouldn't contain more than 1000 characters"
            | v -> Ok(StoryDescription v)

        let value (StoryDescription id) = id

    [<NoComparison; NoEquality>]
    type Story =
        { Root: AggregateRoot<StoryId>
          Title: StoryTitle
          Description: StoryDescription option
          Tasks: TaskEntity.Task list }

    type StoryCreatedEvent =
        { StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option
          CreatedAt: DateTime }

    type StoryUpdatedEvent =
        { StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option
          UpdatedAt: DateTime }

    type StoryDeletedEvent = { StoryId: StoryId }

    type TaskAddedToStoryEvent =
        { StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option
          CreatedAt: DateTime }

    type TaskUpdatedEvent =
        { StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option
          UpdatedAt: DateTime }

    type TaskDeletedEvent = { StoryId: StoryId; TaskId: TaskEntity.TaskId }

    type DomainEvent =
        | StoryCreatedEvent of StoryCreatedEvent
        | StoryUpdatedEvent of StoryUpdatedEvent
        | StoryDeletedEvent of StoryDeletedEvent
        | TaskAddedToStoryEvent of TaskAddedToStoryEvent
        | TaskUpdatedEvent of TaskUpdatedEvent
        | TaskDeletedEvent of TaskDeletedEvent

    let create (id: StoryId) (title: StoryTitle) (description: StoryDescription option) (createdAt: DateTime) : Story * DomainEvent =
        { Root = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
          Title = title
          Description = description
          Tasks = [] },
        DomainEvent.StoryCreatedEvent(
            { StoryId = id
              StoryTitle = title
              StoryDescription = description
              CreatedAt = createdAt }
        )

    let update (story: Story) (title: StoryTitle) (description: StoryDescription option) (updatedAt: DateTime) : Story * DomainEvent =
        let root = { story.Root with UpdatedAt = Some updatedAt }
        let story = { story with Title = title; Description = description; Root = root }
        let event =
            DomainEvent.StoryUpdatedEvent(
                { StoryId = story.Root.Id
                  StoryTitle = title
                  StoryDescription = description
                  UpdatedAt = updatedAt }
            )
        story, event

    let delete (story: Story) : Story * DomainEvent =
        let event = DomainEvent.StoryDeletedEvent({ StoryId = story.Root.Id })
        story, event

    open TaskEntity

    type AddTaskToStoryError = DuplicateTask of TaskId

    let addTaskToStory (story: Story) (task: Task) (createdAt: DateTime) : Result<Story * DomainEvent, AddTaskToStoryError> =
        let duplicate = story.Tasks |> List.exists (equals task)

        if duplicate then
            Error(DuplicateTask task.Entity.Id)
        else
            Ok(
                { story with Tasks = task :: story.Tasks },
                DomainEvent.TaskAddedToStoryEvent(
                    { StoryId = story.Root.Id
                      TaskId = task.Entity.Id
                      TaskTitle = task.Title
                      TaskDescription = task.Description
                      CreatedAt = createdAt }
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
            let entity = { task.Entity with UpdatedAt = Some updatedAt }
            let updatedTask =
                { task with Title = title; Description = description; Entity = entity }
            let tasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = updatedTask :: tasks }
            let event =
                DomainEvent.TaskUpdatedEvent(
                    { StoryId = story.Root.Id
                      TaskId = taskId
                      TaskTitle = title
                      TaskDescription = description
                      UpdatedAt = updatedAt }
                )
            Ok(story, event)
        | None -> Error (TaskNotFound taskId)

    type DeleteTaskError = TaskNotFound of TaskId
    
    let deleteTask (story: Story) (taskId: TaskId) : Result<Story * DomainEvent, DeleteTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let updatedTasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = updatedTasks }
            let event =
                DomainEvent.TaskDeletedEvent({ StoryId = story.Root.Id; TaskId = taskId })
            Ok(story, event)
        | None -> Error (TaskNotFound taskId)

    [<Interface>]
    type IStoryRepository =
        abstract ExistAsync: CancellationToken -> StoryId -> Task<bool>
        abstract GetByIdAsync: CancellationToken -> StoryId -> Task<Story option>
        abstract ApplyEventAsync: CancellationToken -> DomainEvent -> Task<unit>
