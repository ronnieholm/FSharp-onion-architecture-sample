# ADR008: Use ASP.NET directly

## Context

Going with "F# libraries everywhere" can be a net negative.

## Decision

We avoid wrapping ASP.NET with Giraffe or Saturn, as the value proposition isn't
there. As ASP.NET is part of onfrastructure and thus sits at the edge with thin
controller and actions, the benefits of a niche framework aren't there.

## Consequences

Only use mature F# libraries which have a net positive effect on architecture
and complexity.
