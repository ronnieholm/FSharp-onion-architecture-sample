# ADR0003: Thin vs fat domain events

Status: Accepted and active.

## Context

Thin events contain essential fields only: typically Ids and not title,
description, and so on. Because thin events contain mainly references, they
aren't self-contained. An event processor would typically query the aggregate by
Id to get at the information it needs.

Alternatively, we could pass the aggregate as part of the event, but if the
event is preserved in an log, entries could grow large. Which aggregate fields
changed as part of the event would also become implicit.

So instead of

```fsharp
type DomainEvent =
    | StoryCreated of Story
```

we prefer

```fsharp
type BasicStoryDetailsCaptured =
    { DomainEvent: DomainEvent
      StoryId: StoryId
      StoryTitle: StoryTitle
      StoryDescription: StoryDescription option }

type DomainEvent =
    | BasicStoryDetailsCaptured of BasicStoryDetailsCaptured
```

As the events never leave core, they can include domain types. Integration
events, leaving core, would need to be transformed into primitive types.

`OccurredAt` may not be required in some domains, and instead set by the
component persisting the event (if the event needs persisting). Or the value of
`CreatedAt` or `UpdatedAt` would used at event's `OccurredAt` value.

Persisting events for auditing or troubleshooting, some fields should be
promoted to query the events. As each event likely has a different shape, the
event itself is persisted as a string. The aggregate Id to which the event
belongs should be extracted across events and become a field in the persisted
domain events database table.

It's important to note that persisting domain events doesn't imply event
sourcing. Domain events are only intended for use within core, aren't intended
for replay, and aren't versioned. Event sourcing is a complex topic in itself,
not worth it for most applications.

Publishing an event to another aggregate, the notification handler may require
access to fields on the `Story` or `Task`s not part of the event. These handlers
would query the repository. As both the initial command handler and subsequent
notification handlers run within the same transaction, querying the store
guarantees up-to-date entities.

Also, one command handler isn't limited to emitting a single event. Imagine a
use case where the handler adds and removes items to or from a list. This could
result in multiple events.

## Decision

We use semi-fat events, including fields relevant to the event. This supports
using events to update the store as well. It isn't event sourcing so we can't
re-create the state of an aggregate for events, but apply events on the fly.
It's a pragmatic trade-off between going full event sourcing (too complex) and
using a relational database without EF.

We could've stored aggregates and domain events in a document database. It would
simplify the repository implementation by removing the complexities of mapping
from a flat SQL result set to an object graph. In fact, we could provide an
alternative implementation of the repository interface using SQLite as a
document database and core wouldn't be able to tell the difference. Though
complexity might start to creep back in as the aggregates evolve and dynamic
queries would become more difficult, even with SQLite JSON query abilities.

## Consequences

Without EF's build-in change tracker, keeping an aggregate up-to-date with a
relational store is significant work: imagine scanning an aggregate for changes
and generating SQL from those. If instead we treat the store as an immediately
updated read-model, we've simplified change tracking.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
- [Event-Driven Architecture lost its way](https://www.youtube.com/watch?v=YusVrd9rHJU)
- [Domain Model first](https://blog.ploeh.dk/2023/10/23/domain-model-first)