# ADR010: Error value propagation within core

Status: Accepted and active.

## Context

With application layer acting as entry point for hosts such as ASP.NET, it's
where application and domain errors are documented. But being a coordination
layer, executing use cases, the application layer is only directly responsible
for a subset of errors. e.g., failed validation of the request or failed loading
the aggregate due to invalid Id.

Adding a task to a story, for instance, it's up to the domain layer to determine
if the task being added is a duplicate. Similarly, integrating with other
systems, the application layer calls into the infrastructure layer. The specific
service called upon may return errors as specified within the application layer,
sometimes just a boundary error.

Focusing on the interaction between the application and domain layers for add
task to story command, we could imagine the application exposing these possible
errors

```fsharp
type AddTaskToStoryError =
    | AuthorizationError of string
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | BusinessError of string
```

where `BusinessError` is any error returned by the domain layer.

Inside the domain layer we would do as follows and have the application layer
map errors to the `AddTaskToStoryHandlerError` type:

```fsharp
let addTaskToStory (story: Story) (task: Task) (occurredAt: DateTime) : Result<Story * StoryDomainEvent, string> =
    let duplicate = story.Tasks |> List.exists (equals task)
    if duplicate then
        Error "Duplicate task Id"
    else
        Ok(
            { story with Tasks = task :: story.Tasks },
            DomainEvent.TaskAddedToStoryEvent
                { StoryId = story.Root.Id
                  TaskId = task.Entity.Id
                  TaskTitle = task.Title
                  TaskDescription = task.Description
                  CreatedAt = creaoccurredAttedAt }
        )
```

But as error propagation is now stringly typed, we (1) don't have a way to
switch HTTP response code based on the error and (2) as a caller looking at
`AddTaskToStoryHandlerError` it's unclear which errors to expect.

## Decision

We want the application layer `AddTaskToStoryHandlerError` to reflect all
possible error cases, so it must become

```fsharp
type AddTaskToStoryError =
    | AuthorizationError of string
    | ValidationErrors of ValidationError list
    | StoryNotFound of Guid
    | DuplicateTask of Guid
```

Then in `addTaskToStory`, we return an error union type as well:

```fsharp
type AddTaskToStoryError = DuplicateTask of TaskId

let addTaskToStory (story: Story) (task: Task) (occurredAt: DateTime) : Result<Story * StoryDomainEvent, AddTaskToStoryError> =
    let duplicate = story.Tasks |> List.exists (equals task)
    if duplicate then
        Error(DuplicateTask task.Entity.Id)
    else
        Ok(
            { story with Tasks = task :: story.Tasks },
            StoryDomainEvent.TaskAddedToStory
                { DomainEvent = { OccurredAt = occurredAt }
                  StoryId = story.Aggregate.Id
                  TaskId = task.Entity.Id
                  TaskTitle = task.Title
                  TaskDescription = task.Description }
        )
```

Notice how, as we're in the domain layer, we retain `TaskId` instead of
converting it to a `Guid` for the client.

In the application layer we map domain error cases to application error cases:

```fsharp
let fromDomainErrors = function
    | AddTaskToStoryError.DuplicateTask id -> DuplicateTask (TaskId.value id)
```

## Consequences

Defining domain errors and mappers is more work, but now every application layer
error union fully documents the error cases.
