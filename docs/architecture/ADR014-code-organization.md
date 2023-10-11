# ADR014: Code organization

Status: Accepted and active.

## Context

A horizontal slice architecture would result in the following organization:

- Domain.fs
- Application.fs
- Infrastructure.fs
- Program.fs

As file ordering matters to the F# compiler, `Domain.fs` depends on nothing,
`Application.fs` depends on `Domain.fs`, and so on. Similarly, within each
source file definition must precede use.

For a larger application, switching to vertical slice architecture may be
preferred. Here `Story.fs` could contain domain, application, infrastructure,
web, and possibly test code (with similar organization for other aggregates):

- Shared.fs
- Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
- Program.fs

Or Story could be a folder with multiple files or its own assembly.

Vertical slice architecture potentially improves compile times. The F# compiler
is mostly sequential across an assembly, but multiple assemblies may be compiled
in parallel. For this reason, separate assemblies for domain, application,
integration, web, unit test and integration test is a bad idea. Compilation
would become sequential across the solution.

## Decision

To best illustrate the concepts, and until the sample grows sufficiently large,
we stick to horizontal architecture.

## Consequences

Compile time is starting to become a concern.
