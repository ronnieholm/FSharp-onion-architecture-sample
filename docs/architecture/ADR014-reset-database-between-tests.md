# ADR014: Reset database between tests

## Context

No need for Respawn, just issue "delete * table" statements in test class ctor (not Dispose).
No Docker spin-ups (Nick).

Docker containers seem harder to manage in a CI/CD pipeline

## Decision

## Consequences

