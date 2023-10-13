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

    module Int =
        let between (from: int) (to_: int) (v: int) : Result<int, string> =
            if v < from && v > to_ then
                Error $"Should be between {from} and {to_}, both inclusive"
            else
                Ok v

open Validation

module SharedDomain =
    // Even though core is presentation agnostic, we can be inspired by the
    // Zalando API guidelines
    // (https://opensource.zalando.com/restful-api-guidelines/#137), which
    // states that querystring parameters for paging must be named limit and
    // cursor.
    module Paging =
        type Limit = private Limit of int

        module Limit =
            let create (v: int) : Result<Limit, string> = v |> Int.between 1 100 |> Result.map Limit

            let value (Limit v) = v

        type Cursor = private Cursor of string

        module Cursor =
            let create (v: string) : Result<Cursor, string> = v |> String.notNullOrWhitespace |> Result.map Cursor

            let value (Cursor v) = v

    type Paged<'t> = { Cursor: Paging.Cursor option; Items: 't list }

open SharedDomain
open SharedDomain.Paging

module StoryAggregate =
    open Seedwork

    module TaskEntity =
        type TaskId = private TaskId of Guid

        module TaskId =
            let create (v: Guid) : Result<TaskId, string> = v |> Guid.notEmpty |> Result.map TaskId

            let value (TaskId v) = v

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

            let value (TaskTitle v) = v

        type TaskDescription = private TaskDescription of string

        module TaskDescription =
            let create (v: string) : Result<TaskDescription, string> =
                v
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

        let equals (a: Task) (b: Task) : bool = a.Entity.Id = b.Entity.Id

    type StoryId = private StoryId of Guid

    module StoryId =
        let create (v: Guid) : Result<StoryId, string> = v |> Guid.notEmpty |> Result.map StoryId

        let value (StoryId v) = v

    type StoryTitle = StoryTitle of string

    module StoryTitle =
        let create (v: string) : Result<StoryTitle, string> =
            v
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 100)
            |> Result.map StoryTitle

        let value (StoryTitle v) = v

    type StoryDescription = StoryDescription of string

    module StoryDescription =
        let create (v: string) : Result<StoryDescription, string> =
            v
            |> String.notNullOrWhitespace
            |> Result.bind (String.maxLength 1000)
            |> Result.map StoryDescription

        let value (StoryDescription v) = v

    [<NoComparison; NoEquality>]
    type Story =
        { Aggregate: AggregateRoot<StoryId>
          Title: StoryTitle
          Description: StoryDescription option
          Tasks: TaskEntity.Task list }

    // Instead of naming events after CRUD operations, name events after
    // concepts in the business domain. StoryCreated doesn't capture business
    // intent.
    type NewHighLevelRequirementCaptured =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type HighLevelRequirementRevised =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          StoryTitle: StoryTitle
          StoryDescription: StoryDescription option }

    type HighLevelRequirementRemoved = { StoryId: StoryId; OccurredAt: DateTime }

    type NewDetailLevelRequirementCaptured =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type DetailLevelRequirementRevised =
        { DomainEvent: DomainEvent
          StoryId: StoryId
          TaskId: TaskEntity.TaskId
          TaskTitle: TaskEntity.TaskTitle
          TaskDescription: TaskEntity.TaskDescription option }

    type DetailLevelRequirementRemoved = { DomainEvent: DomainEvent; StoryId: StoryId; TaskId: TaskEntity.TaskId }

    type StoryDomainEvent =
        | NewHighLevelRequirementCaptured of NewHighLevelRequirementCaptured
        | HighLevelRequirementRevised of HighLevelRequirementRevised
        | HighLevelRequirementRemoved of HighLevelRequirementRemoved
        | NewDetailLevelRequirementCaptured of NewDetailLevelRequirementCaptured
        | DetailLevelRequirementRevised of DetailLevelRequirementRevised
        | DetailLevelRequirementRemoved of DetailLevelRequirementRemoved

    open TaskEntity

    type CreateStoryError = DuplicateTasks of TaskId list

    let create
        (id: StoryId)
        (title: StoryTitle)
        (description: StoryDescription option)
        (tasks: Task list)
        (createdAt: DateTime)
        (updatedAt: DateTime option)
        : Result<Story * StoryDomainEvent, CreateStoryError> =
        let duplicates =
            tasks
            |> List.groupBy (fun t -> t.Entity.Id)
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
                StoryDomainEvent.NewHighLevelRequirementCaptured
                    { DomainEvent = { OccurredAt = createdAt }
                      StoryId = id
                      StoryTitle = title
                      StoryDescription = description }
            )

    let update (story: Story) (title: StoryTitle) (description: StoryDescription option) (updatedAt: DateTime) : Story * StoryDomainEvent =
        let root = { story.Aggregate with UpdatedAt = Some updatedAt }
        { story with Aggregate = root; Title = title; Description = description },
        StoryDomainEvent.HighLevelRequirementRevised
            { DomainEvent = { OccurredAt = updatedAt }
              StoryId = story.Aggregate.Id
              StoryTitle = title
              StoryDescription = description }

    let delete (story: Story) (occurredAt: DateTime) : StoryDomainEvent =
        // Depending on the specifics of a domain, we might want to explicitly
        // delete the story's tasks and emit task deleted events. In this case,
        // we leave cascade delete to the store.
        StoryDomainEvent.HighLevelRequirementRemoved({ StoryId = story.Aggregate.Id; OccurredAt = occurredAt })

    type AddTaskToStoryError = DuplicateTask of TaskId

    let addTaskToStory (story: Story) (task: Task) (occurredAt: DateTime) : Result<Story * StoryDomainEvent, AddTaskToStoryError> =
        let duplicate = story.Tasks |> List.exists (equals task)
        if duplicate then
            Error(DuplicateTask task.Entity.Id)
        else
            Ok(
                { story with Tasks = task :: story.Tasks },
                StoryDomainEvent.NewDetailLevelRequirementCaptured
                    { DomainEvent = { OccurredAt = occurredAt }
                      StoryId = story.Aggregate.Id
                      TaskId = task.Entity.Id
                      TaskTitle = task.Title
                      TaskDescription = task.Description }
            )

    type UpdateTaskError = TaskNotFound of TaskId

    let updateTask
        (story: Story)
        (taskId: TaskId)
        (title: TaskTitle)
        (description: TaskDescription option)
        (updatedAt: DateTime)
        : Result<Story * StoryDomainEvent, UpdateTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let task = story.Tasks[idx]
            let tasks = story.Tasks |> List.removeAt idx
            let entity = { task.Entity with UpdatedAt = Some updatedAt }
            let updatedTask =
                { task with Entity = entity; Title = title; Description = description }
            let story = { story with Tasks = updatedTask :: tasks }
            Ok(
                story,
                StoryDomainEvent.DetailLevelRequirementRevised
                    { DomainEvent = { OccurredAt = updatedAt }
                      StoryId = story.Aggregate.Id
                      TaskId = taskId
                      TaskTitle = title
                      TaskDescription = description }
            )

        | None -> Error(TaskNotFound taskId)

    type DeleteTaskError = TaskNotFound of TaskId

    let deleteTask (story: Story) (taskId: TaskId) (occurredAt: DateTime) : Result<Story * StoryDomainEvent, DeleteTaskError> =
        let idx = story.Tasks |> List.tryFindIndex (fun t -> t.Entity.Id = taskId)
        match idx with
        | Some idx ->
            let tasks = story.Tasks |> List.removeAt idx
            let story = { story with Tasks = tasks }
            Ok(
                story,
                StoryDomainEvent.DetailLevelRequirementRemoved
                    { DomainEvent = { OccurredAt = occurredAt }
                      StoryId = story.Aggregate.Id
                      TaskId = taskId }
            )
        | None -> Error(TaskNotFound taskId)

    [<Interface>]
    type IStoryRepository =
        abstract ExistAsync: CancellationToken -> StoryId -> Task<bool>
        abstract GetByIdAsync: CancellationToken -> StoryId -> Task<Story option>
        abstract GetStoriesPagedAsync: CancellationToken -> Limit -> Cursor option -> Task<Paged<Story>>
        abstract ApplyEventAsync: CancellationToken -> StoryDomainEvent -> Task<unit>

module DomainService =
    // Services shared across aggregates.
    ()
