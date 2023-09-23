# ADR009: Domain event notifications

## Context

A library like MediatR performs assembly scanning for commands, queries, and
notification handlers. It enables dispatching events, one aggregate indirectly
communicating with another aggregate, around the observer pattern: (1) clients,
command handlers, publish a domain event, (2) MediatR looks up subscribers, (3)
MediatR constructor injects any dependencies into the handler class, (4) MediatR
executing its handler method.

## Decision

With F#, we shy away from assembly scanning and dependency injection frameworks.
Instead, published events may be statically dispatched based on type. A first go
at such implement, with MediatR in mind, might be as follows:

```fsharp
module Notification =
    type DomainEvent = StoryDomainEvent of StoryAggregate.DomainEvent

    // Substitute functions. Actual observers are organized into their own
    // application layer, per aggregate, notification module, similar to
    // commands and queries.
    let createdStoryAsync (_: Seedwork.IAppEnv) (e: StoryCreatedEvent) (_: CancellationToken) =
        printfn $"%A{e.StoryId}"

    let addedTaskToStoryAsync (_: Seedwork.IAppEnv) (e: TaskAddedToStoryEvent) (_: CancellationToken) =
        printfn $"%A{e.StoryId}"

    let publish (env: Seedwork.IAppEnv) event (ct: CancellationToken) =
        match event with
        | StoryDomainEvent e ->
            match e with
            | DomainEvent.StoryCreatedEvent p -> createdStoryAsync env p ct
            | DomainEvent.TaskAddedToStoryEvent p -> addedTaskToStoryAsync env p ct
```

The main issue with the above code is that based on the type of event, we
require a separate dependency from the AppEnv: `createdStoryAsync` requires
access to the story repository, and similarly for events on other aggregates.

On the surface this doesn't look bad because we're passing in every dependency
of the application through AppEnv. AppEnv dependency is passed from the
controller action, to the command handler, and through the `publish`:

```fsharp
module CreateStoryCommand =
    // ...
    let runAsync
        (env: IAppEnv)
        (cmd: CreateStoryCommand)
        (ct: CancellationToken)
        : TaskResult<Guid, CreateStoryHandlerError> =
        taskResult {
            // ...
            do! env.StoryRepository.ApplyEventAsync event ct
            Notification.publish env (Notification.StoryDomainEvent event) ct
            return StoryId.value story.Root.Id
        }
```

Passing AppEnv, function signatures become opaque with respect to dependencies.
From the signature of `runAsync` and `publish`, we cannot infer their
dependencies. Testing has also become more cumbersome as we need to construct an
AppEnv and be aware of which parts to mock based on calls into AppEnv.

Focusing on actual needs, we don't need the flexiblity of the Notification
module. We like notification handlers to run immediately (anot not later on a
queue), so that database changes they make become part of a single transaction.
Thus, directly dispatching from `runAsync` to
`SomeOtherAggregate.SomeEventNotificationAsync` suffices.

Think of each aggregate as a "micro-service" an message passing as in-process
communication between two "services".

## Consequences

The benefit of direct dispatch is that at compile time we know which
notification handlers to dispatch to and therefore which dependencies to pass
into `runAsync`. From prior experience, it usually comes down to a repository
only. We wouldn't update the database or dispatch an email through a
notification handler, for instance. Notifications are only for notifying another
aggregate instead of directly reach into it's state, possibly violating its
invariants.
