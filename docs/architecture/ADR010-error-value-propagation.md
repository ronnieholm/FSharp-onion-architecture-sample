# ADR010: Error value propagation

Status: Accepted and active.

## Context

With the application layer being the point of entry for clients, such as ASP.NET
controllers, it's where application and domain errors should be documented.
Problem is the application layer is a coordination layer, executing use cases.
It's only directly responsible for a subset of errors, such as failed validation
of the request or loading the aggregate.

Adding a task to a story, for instance, the application layer is responsible for
loading the story aggregate by `StoryId`. It's then up to the domain layer to
determine if the task being added is a duplicate, and if not then adding the
task.

Similarly, integrating with other systems, the application layer calls into the
infrastructure layer. The specific service called upon may return error as
specified within the application layer, sometimes just a boundary error.

Focusing on the interaction between the application and domain layers for add
task to story command, imagine these possible errors

```fsharp
type AddTaskToStoryHandlerError =
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | BusinessError of string
```

where `ValidationErrors` is failing to validate the request populated by the
client and `StoryNotFound` is the application layer failing to load the story
aggregate based on the `StoryId` field of the request. Finally, `BusinessError`
is any error returned by the domain layer.

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
        // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies ct event
        return TaskId.value task.Entity.Id
    }
```

Notice how the `create` function returns `BusinessError`. That's actually
incorrect as in this case it cannot fail. It could fail if `create` checked for
dependencies between its arguments. So instead of `Result<Task, string>`, create
should return `Task`.

The real question is how the domain layer should communicate a duplicate task
error to the application layer. Keeping with the `BusinessError` case, inside
the domain layer we could do as follows:

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
HTTP response code based on the error and (2) as a caller looking at
`AddTaskToStoryHandlerError` it's unclear which errors to expect.

## Decision

We want the application layer `AddTaskToStoryHandlerError` to reflect all
possible error cases, so it has to become

```fsharp
type AddTaskToStoryHandlerError =
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | DuplicateTask of Guid
```

We could keep the `Result` type, but with the error case updated: `Result<Story
* DomainEvent, Guid>`. Then in the application layer we map the error case to an
application layer `DuplicateTask`. Downside is that the signature of
`addTaskToStory` doesn't communicate its error case(s). One would have to check
its source to determine when what the Guid error case represents.

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

Notice how, as we're in the domain layer, we retain `TaskId` instead of
converting it to a `Guid` for the clients.

In the application layer, with `fromDomainErrors`, the compiler will ensure we
match on every domain error case, converting it to the application layer
equaivalent of type `Guid`:

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

Defining domain errors and mappers is more work, but now every application layer
error cases are fully documented by the signature of `runAsync`. Adding new
error case, the compiler will identify missing cases.
