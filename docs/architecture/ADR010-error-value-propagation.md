# ADR010: Error value propagation

## Context

With the application layer being the point of entry for clients, it's where
errors should be documented. Problem is the application layer is a coordination
layer, executing use cases. It's only directly responsible for a subset of
errors, such as when validating the request or loading the aggregate.

Adding a task to a story, for instance, the application layer is responsible for
loading the story aggregate by `StoryId`. Then it's up to the domain layer to
determine if the task being added is a duplicate.

Similarly, integrating with other systems, the application layer calls into the
infrastructure layer. The specific service called upon may return error values
as specified within the application layer.

Focusing on the interaction between the application and domain layers for add
task to story command, imagine these possible errors

```fsharp
type AddTaskToStoryHandlerError =
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | BusinessError of string
```

where `ValidationErrors` is the request populated by the client being invalid
and `StoryNotFound` is the application layer failing to load the story aggregate
based on the `StoryId` part of the request. Finally, `BusinessError` is any
error returned by the domain layer.

The application layer is then responsible for mapping errors to
`AddTaskToStoryHandlerError` as follows:

```fsharp
let runAsync
    (stories: IStoryRepository)
    (clock: ISystemClock)
    (ct: CancellationToken)
    (cmd: AddTaskToStoryCommand)
    : TaskResult<Guid, AddTaskToStoryHandlerError> =
    taskResult {
        let! cmd = validate cmd |> Result.mapError ValidationErrors
        let! story =
            stories.GetByIdAsync ct cmd.StoryId
            |> TaskResult.requireSome (StoryNotFound(StoryId.value cmd.StoryId))
        let now = clock.CurrentUtc()
        let! task = create cmd.TaskId cmd.Title cmd.Description now |> Result.mapError BusinessError
        let! _, event = addTaskToStory story task now |> Result.mapError BusinessError
        do! stories.ApplyEventAsync ct event
        // do! SomeOtherAggregate.SomeEventNotificationAsync dependency ct event
        return TaskId.value task.Entity.Id
    }
```

Notice how the `create` function returns `BusinessError`. That's actually
incorrect as in this case it cannot fail. It could fail if `create` checked for
depedencies between its arguments.

Because of the stringly based nature of domain error propagation, we initially
made `create` future proof, but instead of returning `Result<Task, string>` it
should simply return `Task`.

The real question is how the domain layer should communicate a duplicate task to
application layer. Keeping with the `BusinessError` case, inside the domain
layer we'd have we'd have

```fsharp
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
```

As error propagation is stringly typed, we (1) don't have an easy way to switch
HTTP response code based on the error and (2) looking at
`AddTaskToStoryHandlerError` it's unclear which errors the caller should expect.

## Decision

We want the application layer `AddTaskToStoryHandlerError` to reflect all
possible error cases, so it has to become

```fsharp
type AddTaskToStoryHandlerError =
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | DuplicateTask of Guid
```

For a domain function with only one failure path, we could keep the `Result`
type, but with the error case updated: `Result<Story * DomainEvent, Guid>`. Then
in the application layer we could map the error case to application layer
`DuplicateTask`. Downside is that the signature of `addTaskToStory` doesn't
communicate its error case(s). One would have to check its source to determine
when what the Guid error case represents.

A more explicit approach is for domain functions which can error to return a
union, even though oftentimes there's only one error case:

```fsharp
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
```

Notice how, as we're in the domain layer, we retain type `TaskId` instead of
converting it to a `Guid` for the clients.

In the application layer, with `fromDomainErrors`, the compiler will ensure we
match on every domain error case, converting it to the application layer
equaivalent:

```fsharp
let fromDomainErrors = function
    | AddTaskToStoryError.DuplicateTask id -> DuplicateTask (TaskId.value id)

let runAsync
    (stories: IStoryRepository)
    (clock: ISystemClock)
    (ct: CancellationToken)
    (cmd: AddTaskToStoryCommand)
    : TaskResult<Guid, AddTaskToStoryHandlerError> =
    taskResult {
        ...
        let task = create cmd.TaskId cmd.Title cmd.Description now
        let! _, event = addTaskToStory story task now |> Result.mapError fromDomainErrors
        ...
    }
```

## Consequences

It's more work defining domain errors and mappers to the corresponding
application layer errors, but now application layer error cases are fully
documented, and adding new errors the compiler will identify missing cases.
