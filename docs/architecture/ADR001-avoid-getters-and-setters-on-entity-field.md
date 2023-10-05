# ADR001: Avoid getters and setters on entity fields

Status: Accepted and active.

## Context

Fields on Story (aggregate root) and Task (entity) records are public by design.
Code could reach into an instance and update a field, possibly violating an
invariant.

## Decision

Because records are immutable, if code were to update a record directly, it
wouldn't be the end of the world. No other code holding referring to the record
would observe the update.

Only deserialization code should generally bypass record update functions, but
we don't go above and beyond to technically prevent it. Even with classes and
private state, changing internals is possible with reflection. If code wants to,
we can't prevent it.

## Consequences

We stick to records over classes with public properties as they're more
idiomatic F#.
