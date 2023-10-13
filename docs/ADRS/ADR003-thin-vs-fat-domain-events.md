# ADR003: Thin vs fat domain events

Status: Accepted and active.

## Context

Thin events contain essential fields only: typically Ids and not title,
description, and so on. Because thin events contain mainly references, they
aren't self-contained. An event processor would have to use the Ids to query for
the aggregate.

We could pass the aggregate as part of the event, but if the event is preserved
in an log, entries could grow large. Which aggregate fields changed as part of
the event would also become implicit.

So instead of

```fsharp
type DomainEvent =
    | StoryCreated of Story
```

we prefer

```fsharp
type StoryBasicDetailsCaptured =
    { DomainEvent: DomainEvent
      StoryId: StoryId
      StoryTitle: StoryTitle
      StoryDescription: StoryDescription option }

type DomainEvent =
    | StoryBasicDetailsCaptured of StoryCreated
```

As the events never leave core, they can include domain types. Integration
events, leaving core, would have to be converted to primitive types.

`OccurredAt` may not be required in some domains, and instead set by the
component persisting the event (if the event needs persisting).

Persisting events, some fields should be promoted to query the events. As each
event likely has a different shape, the event itself is persisted as a string.
The aggregate Id to which the event belongs should be extracted across events
and become a field in the persisted domain events database table.

Publishing an event to another aggregate, the notification handler may require
access to fields on the `Story` or `Task`s not part of the event. These handlers
would query the repository. As both the initial command handler and subsequent
notification handlers run within the same transaction, querying the store
guarantees up-to-date entities.

Also, one command handler isn't limited to emitting a single event. Imagine a
use case where the handler adds and removes items to or from a list. This would
result in multiple events.

### Comparison to Wlaschin book

Wlaschin injects store operations into the handler as functions. That's a
flexibly, but also atomic approach. We prefer grouping store operations related
to an aggregate into an interface and passing it into the handler. Each type of
event is processed separately, resembling Wlaschin's approach with smaller
functions.

## Decision

We use semi-fat events, including fields relevant to the event. It supports
using events to update the relational store. We don't use event sourcing to
recreate the state of an aggregate, but apply events on the fly.

## Consequences

Without EF's build-in change tracker, keeping an aggregate up-to-date with a
relational is significant work: imagine scanning an aggregate for changes and
generating SQL from those. If instead we treat the store as an immediately
updated read-model, we've simplified change tracking.

Disregarding dynamic queries, storing aggregates in a document database would be
even simpler. At least until aggregates evolve and they'd require versioning.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
- [Event-Driven Architecture lost its way](https://www.youtube.com/watch?v=YusVrd9rHJU)