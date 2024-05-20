# ADR0003: Thin vs fat domain events

Status: Accepted and active.

## Context

Thin events contain essential fields only: typically Ids and not title,
description, and so on, so they aren't self-contained. An event processor would
have to query the aggregate by Id to get at the information it needs.

Alternatively, we could pass the aggregate as part of the event, but if the
event is preserved in an log, entries could grow large. Which aggregate fields
changed as part of the event would also become implicit per event.

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

As events never leave core, they can include domain types. Integration events,
leaving core, would need to be transformed into primitive types.

Persisting events for auditing or troubleshooting, some fields should be
promoted to query events. As each event may have a different shape, the event is
persisted as JSON or a string, depending on database support. The aggregate Id
to which the event belongs should be extracted across events and promoted to a
field in the persisted domain events table.

Persisting domain events doesn't imply event sourcing. Domain events are only
intended for use within core, not intended for replay, and not versioned. Event
sourcing is a complex topic, not worth it for most applications.

Publishing an event to another aggregate, the event processor may require access
to fields not part of the event. These event processors would query the
repository. As both the command handler generating the event and subsequent
event processors run within the same transaction, querying the store guarantees
up-to-date entities.

One command handler isn't limited to emitting a single event. Imagine a use case
where the handler adds and removes items to or from a list. This could result in
multiple events.

## Decision

We use semi-fat events, including fields relevant to the event as it supports
update the store based on events. It isn't event sourcing so we can't re-create
the state of an aggregate for events, but apply events on the fly. It's a
pragmatic trade-off between going full event sourcing (often too complex) and
the complexities of using a relational database without EF change tracking.

We could've stored aggregates and domain events in a document database. It would
remove the repository complexities of mapping from a flat SQL result set to an
object graph. However, complexities start to creep back in as aggregates evolve
and dynamic queries become more difficult, even with SQLite JSON query support.

## Consequences

Without EF's change tracker, keeping an aggregate up-to-date with a relational
store may involve significant work: imagine scanning an aggregate for changes
and generating SQL from those. By instead treating the store as an immediately
updated read-model, we simplify change tracking.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
- [Event-Driven Architecture lost its way](https://www.youtube.com/watch?v=YusVrd9rHJU)
- [Domain Model first](https://blog.ploeh.dk/2023/10/23/domain-model-first)