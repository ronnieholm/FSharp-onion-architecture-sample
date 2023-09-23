# ADR001: Avoid getters and setters on entity fields

Status: Accepted and active.

## Context

Fields on Story (aggregate root) and Task (entity) records are public by design.
Code could reach into an instance and update a field, possibly violating an
invariant.

## Decision

Because records are immutable, code changing fields without going through a
mutator function is of little concern. Except for deserialization code, doing so
is generally wrong, so we don't go above and beyond to technically prevent it.
Even with classes and private state, changing internals is possible using
reflection. If a client want to, we can't prevent it.

## Consequences

We stick to records over classes as they're more idiomatic F# and require less
ceremony.
