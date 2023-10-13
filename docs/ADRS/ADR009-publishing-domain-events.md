# ADR009: Publishing domain events

Status: Accepted and active.

## Context

A library such as MediatR scans the assembly for commands, queries, and
notification handlers. This enables dynamic dispatch of events with one
aggregate communicating with another around the observer pattern: (1) command
handlers publishes an event, (2) save on EF's DbContext triggers publishing, (3)
MediatR looks up subscribers, (3) MediatR constructor injects dependencies into
the notification handler, (4) MediatR executes the notification handler.

This makes each aggregate a "micro-service" with events being in-process sent
between "micro-services".

With F#, we could avoid assembly scanning and most of the steps above by
statically dispatching based on type. A first go at such implementation might
look as follows:

```fsharp
module Notification =
    type DomainEvent = StoryDomainEvent of StoryAggregate.DomainEvent

    // Substitute functions for actual subscribers.
    let captureStoryBasicDetailsAsync (_: Seedwork.IAppEnv) (e: StoryBasicDetailsCaptured) (_: CancellationToken) =
        printfn $"%A{e.StoryId}"

    let taskBasicDetailsAddedToStoryAsync (_: Seedwork.IAppEnv) (e: TaskBasicDetailsAddedToStory) (_: CancellationToken) =
        printfn $"%A{e.StoryId}"

    let publish (env: Seedwork.IAppEnv) event (ct: CancellationToken) =
        match event with
        | StoryDomainEvent e ->
            match e with
            | DomainEvent.StoryBasicDetailsCaptured e -> captureStoryBasicDetailsAsync env e ct
            | DomainEvent.TaskBasicDetailsAddedToStory e -> taskBasicDetailsAddedToStoryAsync env e ct
```

Based on the type of an event, we require one or more dependencies from the
environment. For instance, `captureStoryBasicDetailsAsync` might require access to the story
repository.

## Decision

Focusing on actual needs, though, we don't need the flexibility of the
`Notification` module. We want notification handlers to run immediately anyway,
so that if they make database changes, those changes become part of a single
transaction. Thus, directly dispatching from `runAsync` to
`SomeOtherAggregate.SomeEventNotificationAsync` is even simpler:

```fsharp
let runAsync (env: IAppEnv) (ct: CancellationToken) (cmd: CaptureStoryBasicDetailsCommand) : TaskResult<Guid, CaptureStoryBasicDetailsError> =
    let aux () =
        taskResult {
            do! isInRole env.Identity Member |> Result.mapError AuthorizationError
            let! cmd = validate cmd |> Result.mapError ValidationErrors
            do!
                env.Stories.ExistAsync ct cmd.Id
                |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
            let! story, event =
                StoryAggregate.captureStoryBasicDetails cmd.Id cmd.Title cmd.Description [] (env.Clock.CurrentUtc()) None
                |> Result.mapError fromDomainError
            do! env.Stories.ApplyEventAsync ct event
            // Example of publishing the StoryBasicDetailsCaptured domain event to
            // another aggregate:
            // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies ct event
            // Integration events may be generated here and persisted.
            return StoryId.value story.Aggregate.Id
        }

    runWithDecoratorAsync env.Logger (nameof CaptureStoryBasicDetailsCommand) cmd aux
```

## Consequences

At compile time we know which notification handlers to dispatch to without any
intermediaries.