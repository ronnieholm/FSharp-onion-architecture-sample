module Scrum.Domain

module Seedwork =
    open System
    
    type Entity<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
    type AggregateRoot<'id> = { Id: 'id; CreatedAt: DateTime; UpdatedAt: DateTime option }
    type DomainEvent = { OccurredAt: DateTime }

// Constraint validation on primitive types for reuse across value object
// creations.
module Validation =
    open System
    
    module Guid =
        let notEmpty value = if value = Guid.Empty then Error "Should be non-empty" else Ok value

    module String =
        let notNullOrWhitespace value =
            if String.IsNullOrWhiteSpace(value) then
                Error "Should be non-null, non-empty or non-whitespace"
            else
                Ok value

        let maxLength length (value: string) =
            if value.Length > length then
                Error $"Should contain less than or equal to {length} characters"
            else
                Ok value

    module Int =
        let between from to_ value =
            if value < from && value > to_ then
                Error $"Should be between {from} and {to_}, both inclusive"
            else
                Ok value

open Validation

module Shared =    
    module Paging =
        type Limit = private Limit of int

        module Limit =
            let create value = value |> Int.between 1 100 |> Result.map Limit
            let value (Limit v) : int = v

        type Cursor = private Cursor of string

        module Cursor =
            let create (value: string) = value |> String.notNullOrWhitespace |> Result.map Cursor
            let value (Cursor v) : string = v

        type Paged<'t> = { Cursor: Cursor option; Items: 't list }

module StoryAggregate =
    open System
    open Seedwork

    module TaskEntity =
        type TaskId = private TaskId of Guid

        module TaskId =
            let create value = value |> Guid.notEmpty |> Result.map TaskId
            let value (TaskId v) : Guid = v

        type TaskTitle = private TaskTitle of string

        module TaskTitle =
            let create (v: string) =               
                v
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength 100)
                |> Result.map TaskTitle
            
            let value (TaskTitle v) : string = v

        type TaskDescription = private TaskDescription of string

        module TaskDescription =
            let create (value: string) =
                value
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength 1000)
                |> Result.map TaskDescription

            let value (TaskDescription v) = v

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
        let create value = value |> Guid.notEmpty |> Result.map StoryId
        let value (StoryId v) : Guid = v

    type StoryTitle = StoryTitle of string

    module StoryTitle =
        let create value =
            value
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 100)
            |> Result.map StoryTitle

        let value (StoryTitle v) : string = v

    type StoryDescription = StoryDescription of string

    module StoryDescription =
        let create (value: string) =
            value
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 1000)
            |> Result.map StoryDescription

        let value (StoryDescription v) : string = v

    [<NoComparison; NoEquality>]
    type Story =
        { Aggregate: AggregateRoot<StoryId>
          Title: StoryTitle
          Description: StoryDescription option
          Tasks: TaskEntity.Task list }

    // Instead of naming events after CRUD operations, name events after
    // concepts in the business domain. StoryCreated doesn't capture business
    // intent.
    type BasicStoryDetailsCaptured =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type BasicStoryDetailsRevised =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type StoryRemoved = { StoryId: StoryId; OccurredAt: DateTime }

    type BasicTaskDetailsAddedToStory =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type BasicTaskDetailsRevised =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type TaskRemoved = { DomainEvent: DomainEvent; StoryId: StoryId; TaskId: TaskEntity.TaskId }

    type StoryDomainEvent =
        | BasicStoryDetailsCaptured of BasicStoryDetailsCaptured
        | BasicStoryDetailsRevised of BasicStoryDetailsRevised
        | StoryRemoved of StoryRemoved
        | BasicTaskDetailsAddedToStory of BasicTaskDetailsAddedToStory
        | BasicTaskDetailsRevised of BasicTaskDetailsRevised
        | TaskRemoved of TaskRemoved

    open TaskEntity

    type CaptureBasicStoryDetailsError = DuplicateTasks of TaskId list

    let captureBasicStoryDetails id title description tasks createdAt updatedAt =
        let duplicates =
            tasks
            |> List.groupBy _.Entity.Id
            |> List.filter (fun (_, tasks) -> List.length tasks > 1)
            |> List.map fst
        if List.length duplicates > 0 then
            Error(DuplicateTasks duplicates)
        else
            Ok(
                { Aggregate = { Id = id; CreatedAt = createdAt; UpdatedAt = updatedAt }
                  Title = title
                  Description = description
                  Tasks = tasks },
                StoryDomainEvent.BasicStoryDetailsCaptured
                    { DomainEvent = { OccurredAt = createdAt }
                      StoryId = id
                      StoryTitle = title
                      StoryDescription = description }
            )

    let reviseBasicStoryDetails story title description updatedAt =
        let root = { story.Aggregate with UpdatedAt = Some updatedAt }
        { story with Aggregate = root; Title = title; Description = description },
        StoryDomainEvent.BasicStoryDetailsRevised
            { DomainEvent = { OccurredAt = updatedAt }
              StoryId = story.Aggregate.Id
              StoryTitle = title
              StoryDescription = description }

    let removeStory story occurredAt =
        // Depending on the specifics of a domain, we might want to explicitly
        // delete the story's tasks and emit task deleted events. In this case,
        // we leave cascade delete to the store.
        StoryDomainEvent.StoryRemoved({ StoryId = story.Aggregate.Id; OccurredAt = occurredAt })

    type AddBasicTaskDetailsToStoryError = DuplicateTask of TaskId

    let addBasicTaskDetailsToStory story task occurredAt =
        let duplicate = story.Tasks |> List.exists (equals task)
        if duplicate then
            Error(DuplicateTask task.Entity.Id)
        else
            Ok(
                { story with Tasks = task :: story.Tasks },
                StoryDomainEvent.BasicTaskDetailsAddedToStory
                    { DomainEvent = { OccurredAt = occurredAt }
                      StoryId = story.Aggregate.Id
                      TaskId = task.Entity.Id
                      TaskTitle = task.Title
                      TaskDescription = task.Description }
            )

    type ReviseBasicTaskDetailsError = TaskNotFound of TaskId

    let reviseBasicTaskDetails story taskId title description updatedAt =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let task = story.Tasks[idx]
            let tasks = story.Tasks |> List.removeAt idx
            let entity = { task.Entity with UpdatedAt = Some updatedAt }
            let updatedTask = { Entity = entity; Title = title; Description = description }
            let story = { story with Tasks = updatedTask :: tasks }
            Ok(
                story,
                StoryDomainEvent.BasicTaskDetailsRevised
                    { DomainEvent = { OccurredAt = updatedAt }
                      StoryId = story.Aggregate.Id
                      TaskId = taskId
                      TaskTitle = title
                      TaskDescription = description }
            )

        | None -> Error(TaskNotFound taskId)

    type RemoveTaskError = TaskNotFound of TaskId

    let removeTask story taskId occurredAt =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let tasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = tasks }
            Ok(
                story,
                StoryDomainEvent.TaskRemoved
                    { DomainEvent = { OccurredAt = occurredAt }
                      StoryId = story.Aggregate.Id
                      TaskId = taskId }
            )
        | None -> Error(TaskNotFound taskId)

module DomainService =
    // Services shared across aggregates.
    ()
