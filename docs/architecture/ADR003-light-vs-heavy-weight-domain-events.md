# ADR003: Light vs heavy weight domain events

Status: Accepted and active.

## Context

Light weight domain events contain essential fields only: typically Ids and not
title, description, and so on. Because light weight events contain references
only, they aren't self-contained. To a event processor, possible the entire
aggregate with its properties [1] would have to be available to it.

We could pass the aggregate to a notification handler, but if preserved in an
audit log, what changed may have become implicit and, if persisted to JSON, the
payload could potentially be large. Also, deletions are difficult to express.

## Decision

Instead of

```fsharp
type DomainEvent =
    | StoryCreated of Story
```

do

```fsharp
type StoryCreatedPayload = { StoryId: StoryId; StoryTitle: StoryTitle; StoryDescription: StoryDescription option }
type DomainEvent =
    | StoryCreated of StoryCreatedPayload * DomainEvent.Metadata
```

Given that domain events are processed by core, we can use domain types. For
integration events, we should stick to basic types.

Publishing a domain event may trigger additional publications. If these were to
query the store for the updated `Story` or `Task`, to avoid reading stale state,
the store would have to allow reading uncommitted data.

As a regular handler trigger publishing of events, changes to entities it has
made become part of the current per request transaction, visible to notification
handlers (without the same transation, uncommitted state is available).
Notification handlers may themselves update the database and publish new events.

One command isn't limited to emitting a single domain event. Imagine a use case
where a handler adds and removes items to and from a collection, resulting in
multiple domain events being generated.

## Consequences

Besides an aggregate publishing domain events to other aggregates, we re-use the
same events to synchronize relational database state.

Without EF's build-in state tracking, keeping an aggregate in sync with a
relational store can be significant work: imagine writing code to scan an
aggregate for modifications, generating SQL from it. When instead we treat the
store as an immediately updated read-model, we simplified change tracking. We
can do most of what EF is does without its complexities.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
