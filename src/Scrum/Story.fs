namespace Scrum.Story

module Domain =
    open Scrum.Seedwork.Domain.Validation

    module StoryAggregate =
        open System
        open FsToolkit.ErrorHandling
        open Scrum.Seedwork.Domain

        module TaskEntity =
            type TaskId = private TaskId of Guid

            module TaskId =
                let create value = value |> Guid.notEmpty |> Result.map TaskId
                let value (TaskId v) = v

            type TaskTitle = private TaskTitle of string

            module TaskTitle =
                let maxLength = 100
                let create value =
                    value
                    |> String.notNullOrWhitespace
                    |> Result.bind (String.maxLength maxLength)
                    |> Result.map TaskTitle

                let value (TaskTitle v) = v

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

            let create id title description createdAt =
                { Entity = { Id = id; CreatedAt = createdAt; UpdatedAt = None }
                  Title = title
                  Description = description }

            let equals a b = a.Entity.Id = b.Entity.Id

        type StoryId = private StoryId of Guid

        module StoryId =
            let create value = value |> Guid.notEmpty |> Result.map StoryId
            let value (StoryId v) = v

        type StoryTitle = StoryTitle of string

        module StoryTitle =
            let maxLength = 100
            let create value =
                value
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength maxLength)
                |> Result.map StoryTitle

            let value (StoryTitle v) = v

        type StoryDescription = StoryDescription of string

        module StoryDescription =
            let maxLength = 1000
            let create value =
                value
                |> String.notNullOrWhitespace
                |> Result.bind (String.maxLength maxLength)
                |> Result.map StoryDescription

            let value (StoryDescription v) = v

        [<NoComparison; NoEquality>]
        type Story =
            { Aggregate: AggregateRoot<StoryId>
              Title: StoryTitle
              Description: StoryDescription option
              Tasks: TaskEntity.Task list }

        // Rather than naming events after CRUD operations, they're after
        // concepts in the business domain, i.e., CreateStory doesn't capture
        // business intent well.
        type CaptureBasicStoryDetails =
            { OccurredAt: DateTime
              StoryId: StoryId
              StoryTitle: StoryTitle
              StoryDescription: StoryDescription option }

        type ReviseBasicStoryDetails =
            { OccurredAt: DateTime
              StoryId: StoryId
              StoryTitle: StoryTitle
              StoryDescription: StoryDescription option }

        type RemoveStory =
            { OccurredAt: DateTime
              StoryId: StoryId }

        type AddBasicTaskDetailsToStory =
            { OccurredAt: DateTime
              StoryId: StoryId
              TaskId: TaskEntity.TaskId
              TaskTitle: TaskEntity.TaskTitle
              TaskDescription: TaskEntity.TaskDescription option }

        type ReviseBasicTaskDetails =
            { OccurredAt: DateTime
              StoryId: StoryId
              TaskId: TaskEntity.TaskId
              TaskTitle: TaskEntity.TaskTitle
              TaskDescription: TaskEntity.TaskDescription option }

        type RemoveTask =
            { OccurredAt: DateTime
              StoryId: StoryId
              TaskId: TaskEntity.TaskId }

        // These aren't domain event in the DDD sense. Domain events describe
        // something that has already happened, hence they're named in past
        // tense. These events on the other hand drive changes to the domain.
        //
        // When we have a need for a domain event, say for one aggregate to
        // notify another, we create such domain event upon demand.
        type StoryEvent =
            | CaptureBasicStoryDetails of CaptureBasicStoryDetails
            | ReviseBasicStoryDetails of ReviseBasicStoryDetails
            | RemoveStory of RemoveStory
            | AddBasicTaskDetailsToStory of AddBasicTaskDetailsToStory
            | ReviseBasicTaskDetails of ReviseBasicTaskDetails
            | RemoveTask of RemoveTask

        open TaskEntity

        let zeroStory =
            let zero =
                result {
                    let! storyId = StoryId.create (Guid.NewGuid())
                    let! storyTitle = StoryTitle.create "empty"
                    return
                        { Aggregate = { Id = storyId; CreatedAt = DateTime.MinValue; UpdatedAt = None }
                          Title = storyTitle
                          Description = None
                          Tasks = [] }              
                }
            match zero with
            | Ok zero -> zero
            | Error _ -> unreachable "Invalid zero story"
        
        let apply story =
            function
            | CaptureBasicStoryDetails e ->
                { Aggregate = { Id = e.StoryId; CreatedAt = e.OccurredAt; UpdatedAt = None }
                  Title = e.StoryTitle
                  Description = e.StoryDescription
                  Tasks = [] }
            | ReviseBasicStoryDetails e ->
                let root = { story.Aggregate with UpdatedAt = Some e.OccurredAt }
                { story with Aggregate = root; Title = e.StoryTitle; Description = e.StoryDescription }                
            | RemoveStory _ ->
                story
            | AddBasicTaskDetailsToStory e ->
                let task = create e.TaskId e.TaskTitle e.TaskDescription e.OccurredAt
                { story with Tasks = task :: story.Tasks }
            | ReviseBasicTaskDetails e ->
                let idx = story.Tasks |> List.findIndex (fun t -> t.Entity.Id = e.TaskId)
                let task = story.Tasks[idx]
                let tasks = story.Tasks |> List.removeAt idx
                let entity = { task.Entity with UpdatedAt = Some e.OccurredAt }
                let updatedTask = { Entity = entity; Title = e.TaskTitle; Description = e.TaskDescription }
                { story with Tasks = updatedTask :: tasks }
            | RemoveTask e ->
                let idx = story.Tasks |> List.findIndex (fun t -> t.Entity.Id = e.TaskId)
                let tasks = story.Tasks |> List.removeAt idx
                { story with Tasks = tasks }

        // A list of tasks could be part of the initial story. Then after
        // creation, addBasicTaskDetailsToStory could be called for each. It
        // would make captureBasicStoryDetails return a list of events: one for
        // the story and one for each task. For simplicity, tasks are left out.
        let captureBasicStoryDetails id title description createdAt =
            // Where single field validation is performed by creating value
            // objects, inter-field validation can be performed here.
            CaptureBasicStoryDetails
                { OccurredAt = createdAt
                  StoryId = id
                  StoryTitle = title
                  StoryDescription = description }

        let reviseBasicStoryDetails story title description updatedAt =
            ReviseBasicStoryDetails
                { OccurredAt = updatedAt
                  StoryId = story.Aggregate.Id
                  StoryTitle = title
                  StoryDescription = description }

        let removeStory story occurredAt =
            // Depending on the specifics of a domain, we might want to
            // explicitly delete the story's tasks and emit task deleted
            // events. In this case, we leave cascade delete to the store.
            RemoveStory
                { OccurredAt = occurredAt
                  StoryId = story.Aggregate.Id }

        type AddBasicTaskDetailsToStoryError = DuplicateTask of TaskId

        // Don't pass an externally created Task as it may contain additional
        // state but basic details.
        let addBasicTaskDetailsToStory story taskId title description createdAt =
            let task = create taskId title description createdAt
            let duplicate = story.Tasks |> List.exists (equals task)
            if duplicate then
                Error(DuplicateTask task.Entity.Id)
            else
                AddBasicTaskDetailsToStory
                    { OccurredAt = createdAt
                      StoryId = story.Aggregate.Id
                      TaskId = task.Entity.Id
                      TaskTitle = task.Title
                      TaskDescription = task.Description } |> Ok

        type ReviseBasicTaskDetailsError = TaskNotFound of TaskId

        let reviseBasicTaskDetails story taskId title description updatedAt =
            let exists = story.Tasks |> List.exists (fun t -> t.Entity.Id = taskId)
            match exists with
            | true ->
                ReviseBasicTaskDetails
                    { OccurredAt = updatedAt
                      StoryId = story.Aggregate.Id
                      TaskId = taskId
                      TaskTitle = title
                      TaskDescription = description } |> Ok
            | false -> Error (TaskNotFound taskId)

        type RemoveTaskError = TaskNotFound of TaskId

        let removeTask story taskId occurredAt =
            let exists = story.Tasks |> List.exists (fun t -> t.Entity.Id = taskId)
            match exists with
            | true ->
                RemoveTask
                    { OccurredAt = occurredAt
                      StoryId = story.Aggregate.Id
                      TaskId = taskId } |> Ok
            | false -> Error (TaskNotFound taskId)

    module Service =
        ()

module Application =
    module StoryRequest =
        open System
        open FsToolkit.ErrorHandling
        open Domain
        open Scrum.Seedwork.Domain.Paging
        open Domain.StoryAggregate
        open Domain.StoryAggregate.TaskEntity
        open Scrum.Seedwork.Application.Models
        open Scrum.Seedwork.Application

        type SaveRelationalStoryFromEvent = StoryEvent -> Threading.Tasks.Task<unit>
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

            let runAsync now storyExist (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    do!
                        storyExist cmd.Id
                        |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
                    let event = captureBasicStoryDetails cmd.Id cmd.Title cmd.Description (now ())                    
                    let story = apply zeroStory event
                    // saveStory can be thought of as synchronously updating
                    // the read model based on generated events.
                    do! saveStory event
                    // Example of publishing the StoryBasicDetailsCaptured domain
                    // event to another aggregate:
                    // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies event ct
                    //
                    // Integration events may be generated here and persisted, too.
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

            let runAsync now getStoryById (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        getStoryById cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let event = reviseBasicStoryDetails story cmd.Title cmd.Description (now ())
                    let story = apply story event
                    do! saveStory event
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

            let runAsync now getStoryById (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        getStoryById cmd.Id
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.Id))
                    let event = removeStory story (now ())
                    do! saveStory event
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

            let runAsync now getStoryById (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        getStoryById cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! event =
                        addBasicTaskDetailsToStory story cmd.TaskId cmd.Title cmd.Description (now ())
                        |> Result.mapError fromDomainError
                    let _ = apply story event
                    do! saveStory event
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

            let runAsync now getStoryById (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        getStoryById cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! event =
                        reviseBasicTaskDetails story cmd.TaskId cmd.Title cmd.Description (now ())
                        |> Result.mapError fromDomainError
                    let _ = apply story event
                    do! saveStory event
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

            let runAsync now getStoryById (saveStory: SaveRelationalStoryFromEvent) identity cmd =
                taskResult {
                    do! isInRole identity Member |> Result.requireTrue (AuthorizationError Member)
                    let! cmd = validate cmd |> Result.mapError ValidationErrors
                    let! story =
                        getStoryById cmd.StoryId
                        |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
                    let! event = removeTask story cmd.TaskId (now ()) |> Result.mapError fromDomainError
                    let _ = apply story event
                    do! saveStory event
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

        // Query included to illustrate paging. In practice, we wouldn't query
        // every story. Instead, queries would be for stories in a product
        // backlog, a release backlog, or a sprint backlog, but we don't
        // support organizing stories into a backlog. For a backlog, it would
        // likely only contain StoryIds. Then either the client would lookup
        // storyIds one by one or submit a batch request for StoryIds.
        //
        // In the same vain, GetStoryTasksPagedQuery wouldn't make much
        // business sense. Tasks are cheap to include with stories, so best
        // keep number of queries to a minimum.
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
                        // Per Zalando guidelines, we could write a JsonConverter
                        // to replace "Items" with "Stories".
                        { PagedDto.Cursor = storiesPage.Cursor |> Option.map Cursor.value
                          Items = storiesPage.Items |> List.map StoryDto.from }
                }
    
module Infrastructure =
    exception WebException of string    
    
    let panic message : 't = raise (WebException(message))
    
    module StoryRepository =
        open System
        open System.Data.Common
        open System.Threading
        open System.Collections.Generic
        open System.Data.SQLite
        open FsToolkit.ErrorHandling
        open Scrum.Seedwork.Domain.Paging
        open Domain.StoryAggregate
        open Domain.StoryAggregate.TaskEntity
        open Scrum.Seedwork.Infrastructure
        open Scrum.Seedwork.Infrastructure.Repository

        let parseStory id (r: DbDataReader) =
            // Don't parse by calling StoryAggregate.captureBasicStoryDetails
            // as in general it doesn't guarantee correct construction. When an
            // entity is a state machine, it resets the entity to its starting
            // state. Furthermore, StoryAggregate.captureBasicStoryDetails
            // emits events which we'd be discarding.
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
            // the database. If we wanted to assert invariants not maintained
            // by the database, it requires explicitly code. Integration tests
            // would generally catch such issues, except when the database is
            // updated by a migration.
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
                        // First task on the story. Mark story -> task path
                        // visited.
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

                // For queries involving multiple tables, ADO.NET requires
                // aliasing fields for those to be extractable through the
                // reader.
                let sql = "
                    select s.id s_id, s.title s_title, s.description s_description, s.created_at s_created_at, s.updated_at s_updated_at,
                           t.id t_id, t.title t_title, t.description t_description, t.created_at t_created_at, t.updated_at t_updated_at
                    from stories s
                    left join tasks t on s.id = t.story_id
                    where s.id = @id"
                use cmd = new SQLiteCommand(sql, connection)
                cmd.Parameters.AddWithValue("@id", StoryId.value id |> string) |> ignore

                // Note that ExecuteReader() returns SQLiteDataReader, but
                // ExecuteReaderAsync(...) returns DbDataReader. Perhaps
                // because querying async against SQLite, running in the same
                // address space, makes little async sense. We stick with
                // ExecuteReaderAsync to illustrate how to work with a
                // client/server database.
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

        let applyAsync (transaction: SQLiteTransaction) (ct: CancellationToken) event =
            let connection = transaction.Connection
            task {
                let aggregateId, occuredAt =
                    match event with
                    | CaptureBasicStoryDetails e ->
                        let sql = "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", storyId |> string) |> ignore
                        p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                        p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@createdAt", e.OccurredAt.Ticks) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt
                    | ReviseBasicStoryDetails e ->
                        let sql = "update stories set title = @title, description = @description, updated_at = @updatedAt where id = @id"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                        p.AddWithValue("@description", e.StoryDescription |> Option.map StoryDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@updatedAt", e.OccurredAt.Ticks) |> ignore
                        p.AddWithValue("@id", storyId |> string) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt
                    | RemoveStory e ->
                        let sql = "delete from stories where id = @id"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let storyId = e.StoryId |> StoryId.value
                        cmd.Parameters.AddWithValue("@id", storyId |> string) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt
                    | AddBasicTaskDetailsToStory e ->
                        let sql = "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                        p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@createdAt", e.OccurredAt.Ticks) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt
                    | ReviseBasicTaskDetails e ->
                        let sql = "update tasks set title = @title, description = @description, updated_at = @updatedAt where id = @id and story_id = @storyId"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                        p.AddWithValue("@description", e.TaskDescription |> Option.map TaskDescription.value |> Option.toObj)
                        |> ignore
                        p.AddWithValue("@updatedAt", e.OccurredAt.Ticks) |> ignore
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt
                    | RemoveTask e ->
                        let sql = "delete from tasks where id = @id and story_id = @storyId"
                        use cmd = new SQLiteCommand(sql, connection, transaction)
                        let p = cmd.Parameters
                        let storyId = e.StoryId |> StoryId.value
                        p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                        p.AddWithValue("@storyId", storyId |> string) |> ignore
                        task { do! applyExecuteNonQueryAsync cmd ct } |> ignore
                        storyId, e.OccurredAt

                // We don't serialize an event to JSON because F# discriminated
                // unions aren't supported by System.Text.Json
                // (https://github.com/dotnet/runtime/issues/55744). Instead of
                // a custom converter, or taking a dependency on
                // https://github.com/Tarmil/FSharp.SystemTextJson), we use the
                // F# type printer. This wouldn't work in a pure event sourced
                // system where we'd read back the event for processing, but
                // the printer suffices for persisting event for troubleshooting.
                do!
                    persistEventAsync
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
                let sqlStories = "
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
            
module RouteHandler =
    open System
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.Logging
    open Microsoft.Extensions.Configuration
    open Giraffe
    open Scrum.Seedwork.Infrastructure
    open Scrum.Seedwork.Infrastructure.Repository
    open Scrum.Seedwork.Application
    open Application.StoryRequest
    open Infrastructure
    
    module CaptureBasicStoryDetails =
        open Application.StoryRequest.CaptureBasicStoryDetailsCommand
        
        type Request = { title: string; description: string }

        let handle : HttpHandler =
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
                    let storyExist = StoryRepository.existAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: CaptureBasicStoryDetailsCommand =
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow storyExist storyApply identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("Location", $"/stories/{id}")
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
        open ReviseBasicStoryDetailsCommand
        type Request = { title: string; description: string }

        let handle storyId: HttpHandler =
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
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicStoryDetailsCommand =
                        { Id = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApply identity cmd)

                    match result with
                    | Ok id ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("Location", $"/stories/{id}")
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
        open AddBasicTaskDetailsToStoryCommand
        
        type Request = { title: string; description: string }

        let handle storyId: HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: AddBasicTaskDetailsToStoryCommand =
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApply identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("Location", $"/stories/{storyId}/tasks/{taskId}")
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
        open ReviseBasicTaskDetailsCommand
        
        type Request = { title: string; description: string }
        
        let handle (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let! request = ctx.BindJsonAsync<Request>()
                    let cmd: ReviseBasicTaskDetailsCommand =
                        { StoryId = storyId
                          TaskId = taskId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApply identity cmd)

                    match result with
                    | Ok taskId ->
                        do! transaction.CommitAsync(ctx.RequestAborted)
                        ctx.SetStatusCode 201
                        ctx.SetHttpHeader("Location", $"/stories/{storyId}/tasks/{taskId}")
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
        open RemoveTaskCommand
    
        let handle (storyId, taskId): HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let cmd: RemoveTaskCommand = { StoryId = storyId; TaskId = taskId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApply identity cmd)

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
        open RemoveStoryCommand
    
        let handle storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted
                    let storyApply = StoryRepository.applyAsync transaction ctx.RequestAborted

                    let cmd: RemoveStoryCommand = { Id = storyId }
                    let! result =
                        runWithMiddlewareAsync log identity cmd
                            (fun () -> runAsync utcNow getStoryById storyApply identity cmd)

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
        open GetStoryByIdQuery

        let handle storyId : HttpHandler =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                let configuration = ctx.GetService<IConfiguration>()
                let logger = ctx.GetService<ILogger<_>>()
                let connectionString = configuration.GetConnectionString("Scrum")
                let log = ScrumLogger.log logger
                let identity = UserIdentity.getCurrentIdentity ctx

                task {
                    use connection = getConnection connectionString
                    use transaction = connection.BeginTransaction()
                    let getStoryById = StoryRepository.getByIdAsync transaction ctx.RequestAborted

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
        open Scrum.Seedwork.RouteHandler
        open Application.StoryRequest.GetStoriesPagedQuery
        
        let handle : HttpHandler =
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
                            let getStoriesPaged = StoryRepository.getPagedAsync transaction ctx.RequestAborted

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
