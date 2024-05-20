# ADR0001: Model entities using records

Status: Accepted and active.

## Context

Fields on Story (aggregate root) and Task (non-aggregate root entity) records
are public by default. Code could reach into an instance and update a field,
possibly violating an entity invariant.

## Decision

Because records are immutable, if code were to bypass functions to update a
record, it wouldn't be the end of the world. Other code wouldn't observe the
change.

The alternative is to switch entities to classes. But even with classes and
private state, changing internals is possible with reflection. If code wants to,
we can't prevent it. Sometimes, as with Entity Framework deserialization, using
reflection to set fields on an object is even a feature.

## Consequences

A records over class is idiomatic F# and random code changing state isn't a
practical concern.
