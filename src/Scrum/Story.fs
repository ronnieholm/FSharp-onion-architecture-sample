namespace Scrum.Story

module Domain =
    open Scrum.Shared.Domain.Seedwork.Validation

    module StoryAggregate =
        open System
        open Scrum.Shared.Domain.Seedwork

        module TaskEntity =
            type TaskId = private TaskId of Guid

            module TaskId =
                let create value = value |> Guid.notEmpty |> Result.map TaskId
                let value (TaskId v) : Guid = v

            type TaskTitle = private TaskTitle of string

            module TaskTitle =
                let maxLength = 100
                let create value =
                    value
                    |> String.notNullOrWhitespace
                    |> Result.bind (String.maxLength maxLength)
                    |> Result.map TaskTitle

                let value (TaskTitle v) : string = v

            type TaskDescription = private TaskDescription of string

            module TaskDescription =
                let maxLength = 1000
                let create value =
                    value
                    |> String.notNullOrWhitespace
                    |> Result.bind (String.maxLength maxLength)
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
                : Task =
                { Entity = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
                  Title = title
                  Description = description }

            let equals a b = a.Entity.Id = b.Entity.Id

        type StoryId = private StoryId of Guid

        module StoryId =
            let create value = value |> Guid.notEmpty |> Result.map StoryId
            let value (StoryId v) : Guid = v

        type StoryTitle = StoryTitle of string

        module StoryTitle =
            let maxLength = 100
            let create value =
                value
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength maxLength)
                |> Result.map StoryTitle

            let value (StoryTitle v) : string = v

        type StoryDescription = StoryDescription of string

        module StoryDescription =
            let maxLength = 1000
            let create value =
                value
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength maxLength)
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

        type StoryRemoved =
            { DomainEvent: DomainEvent
              StoryId: StoryId }

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

        // A list of tasks could be part of the initial story. Then after creation,
        // addBasicTaskDetailsToStory could be called for each. It would make
        // captureBasicStoryDetails return a list of events: one for the story and
        // one for each task. For simplicity, tasks are left out.
        let captureBasicStoryDetails id title description createdAt =
            { Aggregate = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
              Title = title
              Description = description
              Tasks = [] },
            BasicStoryDetailsCaptured
                { DomainEvent = { OccurredAt = createdAt }
                  StoryId = id
                  StoryTitle = title
                  StoryDescription = description }

        let reviseBasicStoryDetails story title description updatedAt =
            let root = { story.Aggregate with UpdatedAt = Some updatedAt }
            { story with Aggregate = root; Title = title; Description = description },
            BasicStoryDetailsRevised
                { DomainEvent = { OccurredAt = updatedAt }
                  StoryId = story.Aggregate.Id
                  StoryTitle = title
                  StoryDescription = description }

        let removeStory story occurredAt =
            // Depending on the specifics of a domain, we might want to explicitly
            // delete the story's tasks and emit task deleted events. In this case,
            // we leave cascade delete to the store.
            StoryRemoved
                { DomainEvent = { OccurredAt = occurredAt }
                  StoryId = story.Aggregate.Id }

        type AddBasicTaskDetailsToStoryError = DuplicateTask of TaskId

        // Don't pass an externally created Task as it may contain additional state
        // but basic details.
        let addBasicTaskDetailsToStory story taskId title description createdAt =
            let task = TaskEntity.create taskId title description createdAt
            let duplicate = story.Tasks |> List.exists (equals task)
            if duplicate then
                Error(DuplicateTask task.Entity.Id)
            else
                Ok(
                    { story with Tasks = task :: story.Tasks },
                    BasicTaskDetailsAddedToStory
                        { DomainEvent = { OccurredAt = createdAt }
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
                    BasicTaskDetailsRevised
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
                    TaskRemoved
                        { DomainEvent = { OccurredAt = occurredAt }
                          StoryId = story.Aggregate.Id
                          TaskId = taskId }
                )
            | None -> Error(TaskNotFound taskId)

    module Service =
        // Services shared across aggregates.
        ()

module Application =
    module StoryRequest =
        open System
        open FsToolkit.ErrorHandling
        open Domain
        open Scrum.Shared.Domain.Shared.Paging
        open Domain.StoryAggregate
        open Domain.StoryAggregate.TaskEntity
        open Scrum.Shared.Application.Models
        open Scrum.Shared.Application.Seedwork

        type ApplyEvent = StoryDomainEvent -> Threading.Tasks.Task<unit>
        type GetPaged = Limit -> Cursor option -> Threading.Tasks.Task<Paged<Story>>

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
                    do! storyApplyEvent event
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
                    do! storyApplyEvent event
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
                    do! storyApplyEvent event
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
                    do! storyApplyEvent event
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
                    do! storyApplyEvent event
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
                    do! storyApplyEvent event
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
                        // replace "Items" with "Stories".
                        { PagedDto.Cursor = storiesPage.Cursor |> Option.map Cursor.value
                          Items = storiesPage.Items |> List.map StoryDto.from }
                }
    
module Infrastructure =
    exception WebException of string    
    
    let panic message : 't = raise (WebException(message))
    
    module SqliteStoryRepository =
        open System
        open System.Data.Common
        open System.Threading
        open System.Collections.Generic
        open System.Data.SQLite
        open FsToolkit.ErrorHandling
        open Scrum.Shared.Domain.Shared.Paging
        open Domain.StoryAggregate
        open Domain.StoryAggregate.TaskEntity
        open Scrum.Shared.Infrastructure.Seedwork
        open Scrum.Shared.Infrastructure.Seedwork.Repository

        let parseStory id (r: DbDataReader) =
            // Don't parse by calling StoryAggregate.captureBasicStoryDetails as
            // in general it doesn't guarantee correct construction. When an
            // entity is a state machine, it resets the entity to its starting
            // state. Furthermore, StoryAggregate.captureBasicStoryDetails emits
            // events which we'd be discarding.
            { Aggregate = {
                Id = id
                CreatedAt = parseCreatedAt r["s_created_at"]
                UpdatedAt = parseUpdatedAt r["s_updated_at"] }
              Title = (r["s_title"] |> string |> StoryTitle.create |> panicOnError "s_title")
              Description =
                (Option.ofDBNull r["s_description"]
                |> Option.map (string >> StoryDescription.create >> panicOnError "s_description"))
              Tasks = [] }

        let parseTask id (r: DbDataReader) =
            // We know tasks are unique based on the primary key constraint in
            // the database. If we wanted to assert invariants not maintained by
            // the database, it requires explicitly code. Integration tests would
            // generally catch such issues, except when the database is updated by
            // a migration.
            { Entity = {
                Id = id
                CreatedAt = parseCreatedAt r["t_created_at"]
                UpdatedAt = parseUpdatedAt r["t_updated_at"] }
              Title = (r["t_title"] |> string |> TaskTitle.create |> panicOnError "t_title")
              Description =
                (Option.ofDBNull r["t_description"]
                |> Option.map (string >> TaskDescription.create >> panicOnError "t_description")) }


        let storiesToDomainAsync ct (r: DbDataReader) =
            // See
            // https://github.com/ronnieholm/Playground/tree/master/FlatToTreeStructure
            // for details on the flat table to tree deserialization algorithm.
            let storyTasks = Dictionary<StoryId, Dictionary<TaskId, Task>>()
            let storyTasksOrder = Dictionary<StoryId, ResizeArray<TaskId>>()
            let visitTask storyId =
                let taskId = r["t_id"]
                if taskId <> DBNull.Value then
                    let taskId = taskId |> string |> Guid |> TaskId.create |> panicOnError "t_id"
                    let task = parseTask taskId r

                    let storyVisited, tasks = storyTasks.TryGetValue(storyId)
                    if not storyVisited then
                        // First task on the story. Mark story -> task path visited.
                        let tasks = Dictionary<_, _>()
                        tasks.Add(taskId, task)
                        storyTasks.Add(storyId, tasks)

                        let order = ResizeArray<_>()
                        order.Add(taskId)
                        storyTasksOrder.Add(storyId, order)
                    else
                        // Non-first task on the story. Mark task path under
                        // existing story visited.
                        let taskVisited, _ = tasks.TryGetValue(taskId)
                        if not taskVisited then
                            tasks.Add(taskId, task)
                            let order = storyTasksOrder[storyId]
                            order.Add(taskId)

            // Dictionary doesn't maintain insertion order, so combine with
            // ResizeArray to support SQL with order clause.
            let stories = Dictionary<StoryId, Story>()
            let storiesOrder = ResizeArray<StoryId>()
            let visitStory () =
                let id = r["s_id"] |> string |> Guid |> StoryId.create |> panicOnError "s_id"
                let ok, _ = stories.TryGetValue(id)
                if not ok then
                    let story = parseStory id r
                    stories.Add(id, story)
                    storiesOrder.Add(id)
                visitTask id

            task {
                while! r.ReadAsync(ct) do
                   visitStory ()

                assert(stories.Count = storiesOrder.Count)
                assert(storyTasks.Count = storyTasksOrder.Count)

                return
                    storiesOrder
                    |> Seq.map (fun storyId ->
                        let story = stories[storyId]
                        let tasks =
                            let ok, order = storyTasksOrder.TryGetValue(storyId)
                            if ok then
                                order
                                |> Seq.map (fun taskId -> storyTasks[storyId][taskId])
                                |> Seq.toList
                            else
                                []
                        { story with Tasks = Seq.toList tasks })
                    |> Seq.toList
            }

        let getByIdAsync (transaction: SQLiteTransaction) (ct: CancellationToken) id =
            task {
                let connection = transaction.Connection

                // For queries involving multiple tables, ADO.NET requires aliasing
                // fields for those to be extractable through the reader.
                let sql =
                    "
                    select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                           t.id t_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                    from stories s
                    left join tasks t on s.id = t.story_id
                    where s.id = @id"
                use cmd = new SQLiteCommand(sql, connection)
                cmd.Parameters.AddWithValue("@id", StoryId.value id |> string) |> ignore

                // Note that ExecuteReader() returns SQLiteDataReader, but
                // ExecuteReaderAsync(...) returns DbDataReader. Perhaps because
                // querying async against SQLite, running in the same address space,
                // makes little async sense. We stick with ExecuteReaderAsync to
                // illustrate how to work with a client/server database.
                let! reader = cmd.ExecuteReaderAsync(ct)
                let! stories = storiesToDomainAsync ct reader
                return
                    (let count = stories |> List.length
                     match count with
                     | 0 -> None
                     | 1 -> stories |> List.exactlyOne |> Some
                     | _ -> panic $"Invalid database. {count} instances with story Id: '{StoryId.value id}'")
            }

        let existAsync (transaction: SQLiteTransaction) (ct: CancellationToken) id =
            task {
                let connection = transaction.Connection
                let sql = "select count(*) from stories where id = @id"
                use cmd = new SQLiteCommand(sql, connection, transaction)
                cmd.Parameters.AddWithValue("@id", id |> StoryId.value |> string) |> ignore
                let! count = cmd.ExecuteScalarAsync(ct)
                return
                    (match count :?> int64 with
                     | 0L -> false
                     | 1L -> true
                     | _ -> panic $"Invalid database. {count} instances with story Id: '{StoryId.value id}'")
            }

        // Compared to event sourcing we aren't storing commands, but events from
        // applying the commands. We don't have to worry about the shape of events
        // evolving over time; only to keep the store up to date.
        let applyEventAsync (transaction: SQLiteTransaction) (ct: CancellationToken) event =
            let connection = transaction.Connection
            task {
                let aggregateId, occuredAt =
                    match event with
                    | BasicStoryDetailsCaptured e ->
                        let sql =
                            "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", storyId |> string) |> ignore
                        p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                        p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@createdAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt
                    | BasicStoryDetailsRevised e ->
                        let sql =
                            "update stories set title = @title, description = @description, updated_at = @updatedAt where id = @id"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                        p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@updatedAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                        p.AddWithValue("@id", storyId |> string) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt
                    | StoryRemoved e ->
                        let sql = "delete from stories where id = @id"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let storyId = e.StoryId |> StoryId.value
                        cmd.Parameters.AddWithValue("@id", storyId |> string) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt
                    | BasicTaskDetailsAddedToStory e ->
                        let sql =
                            "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                        p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@createdAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt
                    | BasicTaskDetailsRevised e ->
                        let sql =
                            "update tasks set title = @title, description = @description, updated_at = @updatedAt where id = @id and story_id = @storyId"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                        p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@updatedAt", e.DomainEvent.OccurredAt.Ticks) |> ignore
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt
                    | TaskRemoved e ->
                        let sql = "delete from tasks where id = @id and story_id = @storyId"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        task { do! applyEventExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.DomainEvent.OccurredAt

                // We don't serialize an event to JSON because F# discriminated
                // unions aren't supported by System.Text.Json
                // (https://github.com/dotnet/runtime/issues/55744). Instead of a
                // custom converter, or taking a dependency on
                // https://github.com/Tarmil/FSharp.SystemTextJson), we use the F#
                // type printer. This wouldn't work in a pure event sourced system
                // where we'd read back the event for processing, but the printer
                // suffices for persisting domain event for troubleshooting.
                do!
                    persistDomainEventAsync
                        transaction
                        ct
                        (nameof Story)
                        aggregateId
                        (event.GetType().Name)
                        $"%A{event}"
                        occuredAt
            }

        let getPagedAsync (transaction: SQLiteTransaction) (ct: CancellationToken) limit cursor =
            let connection = transaction.Connection
            task {
                let sqlStories =
                    "
                    select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                           t.id t_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                    from stories s
                    left join tasks t on s.id = t.story_id
                    where s.created_at > @cursor
                    order by s.created_at
                    limit @limit"
                let cursor = cursorToOffset cursor
                use cmd = new SQLiteCommand(sqlStories, connection)
                cmd.Parameters.AddWithValue("@cursor", cursor) |> ignore
                cmd.Parameters.AddWithValue("@limit", Limit.value limit) |> ignore
                let! reader = cmd.ExecuteReaderAsync(ct)
                let! stories = storiesToDomainAsync ct reader

                if stories.Length = 0 then
                    return { Cursor = None; Items = [] }
                else
                    let pageEndOffset = stories[stories.Length - 1].Aggregate.CreatedAt.Ticks
                    let! globalEndOffset = getLargestCreatedAtAsync "stories" connection transaction ct
                    let cursor = offsetsToCursor pageEndOffset globalEndOffset
                    return { Cursor = cursor; Items = stories }
            }
            
module Web =
    open System
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.Logging
    open Microsoft.Extensions.Configuration
    open Giraffe
    open Scrum.Shared.Infrastructure.Seedwork
    open Scrum.Shared.Application.Seedwork
    open Application.StoryRequest
    open Scrum.Shared.Infrastructure.Seedwork.Repository

    module CaptureBasicStoryDetails =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open Application.StoryRequest.CaptureBasicStoryDetailsCommand
        
        type Request = { title: string; description: string }

        let handler : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                // TODO: verify no query string args passed
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")

                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let storyExist = SqliteStoryRepository.existAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: CaptureBasicStoryDetailsCommand =
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow storyExist storyApplyEvent identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{id}") // TODO: should headers be capitalized?
                        return! json {| StoryId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | DuplicateStory id -> unreachable (string id)
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }
    
    module ReviseBasicStoryDetails =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open ReviseBasicStoryDetailsCommand
        type Request = { title: string; description: string }

        let handler storyId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                // TODO: verify no query string args passed
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicStoryDetailsCommand =
                        { Id = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{id}")
                        return! json {| StoryId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module AddBasicTaskDetailsToStory =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open AddBasicTaskDetailsToStoryCommand
        
        type Request = { title: string; description: string }

        let handler storyId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: AddBasicTaskDetailsToStoryCommand =
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{storyId}/tasks/{taskId}")
                        return! json {| TaskId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | DuplicateTask id -> unreachable (string id)
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module ReviseBasicTaskDetails =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open ReviseBasicTaskDetailsCommand
        
        type Request = { title: string; description: string }
        
        let handler (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicTaskDetailsCommand =
                        { StoryId = storyId
                          TaskId = taskId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("location", $"/stories/{storyId}/tasks/{taskId}")
                        return! json {| TaskId = id |} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module RemoveTask =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open RemoveTaskCommand
    
        let handler (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let cmd: RemoveTaskCommand = { StoryId = storyId; TaskId = taskId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                            | TaskNotFound id -> ProblemDetails.create 404 $"Task not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module RemoveStory =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open RemoveStoryCommand
    
        let handler storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApplyEvent = SqliteStoryRepository.applyEventAsync transaction ctx.RequestAborted

                    let cmd: RemoveStoryCommand = { Id = storyId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApplyEvent identity cmd)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound _ -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }
                
    module GetStoryById =
        open Scrum.Shared.Infrastructure
        open Infrastructure
        open GetStoryByIdQuery

        let handler storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = SqliteStoryRepository.getByIdAsync transaction ctx.RequestAborted

                    let qry: GetStoryByIdQuery = { Id = storyId }
                    let! result =
                        runWithMiddlewareAsync log identity qry
                            (fun () -> runAsync getStoryById identity qry)

                    match result with
                    | Ok _ ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 200
                        return! json {||} next ctx
                    | Error e ->
                        do! transaction.RollbackAsync(ctx.RequestAborted)
                        let problem =
                            match e with
                            | AuthorizationError role -> ProblemDetails.authorizationError role
                            | ValidationErrors ve -> ProblemDetails.validationErrors ve
                            | StoryNotFound id -> ProblemDetails.create 404 $"Story not found: '{string id}'"
                        ctx.SetStatusCode problem.Status
                        ctx.SetContentType (ProblemDetails.inferContentType ctx.Request.Headers.Accept)
                        return! json problem next ctx
                }

    module GetStoriesPaged =
        open FsToolkit.ErrorHandling        
        open Scrum.Shared.Infrastructure
        open Scrum.Shared.Web
        open Application.StoryRequest.GetStoriesPagedQuery
        open Infrastructure
        
        let handler : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    let! result =
                        taskResult {
                            let! limit =
                                ctx.GetQueryStringValue "limit"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "limit")
                                |> Result.bind (stringToInt32 "limit")
                            let! cursor =
                                ctx.GetQueryStringValue "cursor"
                                |> Result.mapError (fun _ -> ProblemDetails.missingQueryStringParameter "cursor")
                            let! _ = verifyOnlyExpectedQueryStringParameters ctx.Request.Query [ nameof limit; nameof cursor ]

                            use connection = getConnection connectionString
                            use transaction = connection.BeginTransaction()
                            let getStoriesPaged = SqliteStoryRepository.getPagedAsync transaction ctx.RequestAborted

                            let qry: GetStoriesPagedQuery = { Limit = limit; Cursor = cursor |> Option.ofObj }
                            let! result =
                                runWithMiddlewareAsync log identity qry
                                    (fun () -> runAsync getStoriesPaged identity qry)
                                |> TaskResult.mapError(
                                        function
                                        | AuthorizationError role -> ProblemDetails.authorizationError role
                                        | ValidationErrors ve -> ProblemDetails.validationErrors ve)
                            do! transaction.RollbackAsync(ctx.RequestAborted)
                            return result
                        }

                    return! toPagedResult result ctx next
                }
