# ADR0003: Thin vs fat domain events

Status: Accepted and active.

## Context

Thin events contain essential fields only: typically Ids and not title,
description, and so on, so they aren't self-contained. An event processor would
usually have to query the aggregate by Id to get at the information it needs.

Alternatively, we could make the aggregate as part of the event, but if the
event is preserved in a log, entries could grow large. Which aggregate fields
changed as part of the event would also become implicit per event.

So instead of

```fsharp
type DomainEvent =
    | StoryCreated of Story
```

prefer

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

Persisting domain events for auditing or troubleshooting is rarely useful for
event sourced solution. They're only intended for use within core, not intended
for replay, and not versioned.

In a non-event sourced system, some domain event fields should be promoted to
database fields for querying. As each event may have a different shape, the
event is persisted as JSON or a string, depending on database support. Aggregate
Id to which the event belongs is one such promoted field in persisted domain
events table.

Publishing an event to another aggregate, the event processor may require access
to fields not part of the event. As such, it would query the repository for
additional information. As both the command handler generating the event and
subsequent event processors run within the same transaction, querying the store
guarantees up-to-date entities.

One command handler isn't limited to emitting a single event. Imagine a workflow
where the handler adds and removes items to or from a list. This could result in
multiple events, one for each item.

## Decision

Use semi-fat events, including only fields relevant to the event.

## Consequences

Semi-fat events strikes the best balance between including too little and too
much information.

## See also

- [Domain events: Design and
  implementation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
- [Event-Driven Architecture lost its way](https://www.youtube.com/watch?v=YusVrd9rHJU)
- [Domain Model first](https://blog.ploeh.dk/2023/10/23/domain-model-first)