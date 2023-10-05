# ADR009: Domain event notifications

Status: Accepted and active.

## Context

A library such as MediatR performs assembly scanning for commands, queries, and
notification handlers. This enables dispatching events, one aggregate indirectly
communicating with another, around the observer pattern: (1) command handlers,
publish a domain event, (2) MediatR looks up subscribers, (3) MediatR
constructor injects dependencies into a new handler instance, (4) MediatR
executes its handler method.

Think of each aggregate as a "micro-service" and message passing as in-process
communication between two "services".

## Decision

With F#, we want to avoid assembly scanning and dependency injection frameworks.
Instead, published an event may be done by statically dispatching based on type.
A first go at such implementation, with MediatR in mind, might be as follows:

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

The issue with the above code is that based on the type of event, we require one
or more dependencies from the `AppEnv`: `createdStoryAsync` might requireq
access to the story repository, and similarly for other events.

On the surface this doesn't look too bad if we're passing in every dependency of
the application through AppEnv. AppEnv dependency is passed from the controller
action to the command handler to the `publish` function:

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

While passing `AppEnv` around makes function signatures shorter, it also makes
them opaque with respect to dependencies. From the signature of `runAsync` and
`publish`, we cannot infer their dependencies: it could be none, it could all of
`AppEnv`. Testing also becomes more cumbersome as we need to construct an
`AppEnv` and be manually aware of which parts to fake. The compiler doesn't
guide us.

Focusing on actual needs, we don't need the flexibility of the `Notification`
module above. We want notification handlers to run immediately, so that if they
make database changes, they become part of a single transaction. Thus, directly
dispatching from `runAsync` to `SomeOtherAggregate.SomeEventNotificationAsync`
suffices.

## Consequences

The benefits of direct dispatch is that at compile time we know which
notification handlers to dispatch to and therefore which dependencies to pass
into `runAsync`. From prior experience, it usually comes down to a repository.
We shouldn't dispatch an email through a notification handler, for instance.
Notifications are for notifying another aggregate instead of directly reaching
into it, possibly violating its invariants.
