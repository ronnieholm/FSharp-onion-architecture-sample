# ADR001: Model entities using records

Status: Accepted and active.

## Context

Fields on Story (aggregate root) and Task (entity) records are public by design.
Code could reach into an instance and update a field, possibly violating an
entity invariant.

## Decision

Because records are immutable, if code were to bypass functions to update a
record, it wouldn't be the end of the world. Other code referring to the record
wouldn't observe the change.

The alternative would be switching entities to classes. Still, even with classes
and private state, changing internals is possible with reflection. If code wants
to, we can't prevent it. And sometimes, as with Entity Framework
deserialization, using reflection to set fields on an object is a feature.

## Consequences

Records over classes is idiomatic F# and random code changing state isn't a
practical concern.
