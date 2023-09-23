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

        let create (id: TaskId) (title: TaskTitle) (description: TaskDescription option) (createdAt: DateTime) : Result<Task, string> =
            Ok
                { Entity = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
                  Title = title
                  Description = description }

        let equals a b = a.Entity.Id = b.Entity.Id

    type StoryId = StoryId of Guid

    module StoryId =
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

    type TaskAddedToStoryEvent =
        { TaskId: TaskEntity.TaskId
          StoryId: StoryId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option
          CreatedAt: DateTime }

    type DomainEvent =
        | StoryCreatedEvent of StoryCreatedEvent
        | TaskAddedToStoryEvent of TaskAddedToStoryEvent

    let create
        (id: StoryId)
        (title: StoryTitle)
        (description: StoryDescription option)
        (createdAt: DateTime)
        : Result<Story * DomainEvent, string> =
        Ok(
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
        )

    open TaskEntity

    let addTaskToStory (story: Story) (task: Task) (createdAt: DateTime) : Result<Story * DomainEvent, string> =
        let duplicate = story.Tasks |> List.exists (equals task)

        if duplicate then
            Error "Duplicate task Id"
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

    [<Interface>]
    type IStoryRepository =
        abstract ExistAsync: CancellationToken -> StoryId -> Task<bool>
        abstract GetByIdAsync: CancellationToken -> StoryId -> Task<Story option>
        abstract ApplyEventAsync: CancellationToken -> DomainEvent -> Task<unit>
