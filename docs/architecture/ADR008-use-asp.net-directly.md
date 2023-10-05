# ADR008: Use ASP.NET directly

Status: Accepted and active.

## Context

Going with "F# libraries everywhere" is usally a net negative.

## Decision

We avoid wrapping ASP.NET with Giraffe or Saturn, as the value proposition isn't
there. With ASP.NET part of the infrastructure layer, and thus at the edge of
the onion, the benefits of a niche framework aren't there.

## Consequences

Only use mature F# libraries which have a long term net positive effect on
architecture and complexity.
