# ADR008: Prefer ASP.NET over Giraffe

Status: Accepted and active.

## Context

Going with "F# libraries everywhere" is usually a net negative. For the web part
of the infrastructure layer, we avoid wrapping ASP.NET with Giraffe or Saturn.
Going functional is a net negative because the size of the user base,
maintenance, and documentation isn't on par with plain ASP.NET.

## Decision

As ASP.NET resides in the infrastructure layer, at the edge of the onion,
there's less of a benefit to going functional compared to within the application
core.

## Consequences

Only use mature F# libraries which have a long term net positive effect on
architecture and complexity.
