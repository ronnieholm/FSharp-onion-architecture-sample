# ADR003: Light vs heavy domain events

Status: Accepted and active.

## Context

Light domain events contain essential fields only: typically Ids and not title,
description, and so on. Because light events contain references only, they
aren't self-contained. To an event processor, the entire aggregate would have to
be available.

We could pass the aggregate as part of the event, but if preserved in an log,
entries could be large. Which aggregate fields changed as part of the event
would have become implicit as well. Also, deletions would be difficult to
express.

## Decision

Instead of

```fsharp
type DomainEvent =
    | StoryCreated of Story
```

we prefer

```fsharp
type StoryCreated =
    { StoryId: StoryId
      StoryTitle: StoryTitle
      StoryDescription: StoryDescription option
      OccurredAt: DateTime }

type DomainEvent =
    | StoryCreated of StoryCreated
```

As domain events are processed by core, we can include domain types. For
integration events, leaving core, we'd have to stick to basic types.

Publishing a domain event may trigger additional publications. If these
notification handlers require the updated `Story` or `Task` entities, they would
query the repository. As both the initial command handler and notification
handlers run within the same transaction, querying the store up-to-date
entities.

One command handler isn't limited to emitting a single domain event. Imagine a
use case where the command handler adds and removes items to or from a list.
This would result in multiple domain events being generated.

## Consequences

Besides an aggregate publishing domain events to other aggregates, we re-use the
events to update the relational database state.

Without EF's build-in change tracking, keeping an aggregate up-to-date with a
relational store can be significant work: imagine scanning an aggregate for
modifications, generating SQL from it. If instead we treat the store as an
immediately updated read-model, we've simplified change tracking. We now can do
most of what EF does without its complexities.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
