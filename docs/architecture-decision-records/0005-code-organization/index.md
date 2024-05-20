# ADR0005: Code organization

Status: Accepted and active.

## Context

A horizontal slice architecture results in (a variation of) the following
organization:

- Domain.fs
- Application.fs
- Infrastructure.fs
- Program.fs

File ordering matters to the F# compiler, so `Domain.fs` depends on nothing,
`Application.fs` depends on `Domain.fs`, and so on. Similarly, within each
source file, definition must precede use.

For larger applications, switching to vertical slice architecture may be
preferred. With vertical slice architecture, `Story.fs` (or a variation thereof)
would contain domain, application, infrastructure, web, and maybe even test
code:

- Shared.fs
- Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
- Program.fs

`Story.fs` could also be a folder with multiple files or its own assembly for
faster compile times.

Vertical slice architecture potentially improves compile times. The F# compiler
is mostly sequential across an assembly with multiple assemblies compiled in
parallel. For this reason, organizing code into dependent assemblies for domain,
application, integration, web, unit test, and integration test is a bad idea.
Compilation becomes sequential across the solution.

## Decision

To best illustrate the concepts, and until the sample grows sufficiently large,
we use horizontal architecture. We also stick with one F# file per layer of the
onion, even though compared to typical C# files, F# files are large. Roughly
speaking, each module within each file would correspond to a class in F# (and
nested modules to subfolders). With types defined in dependency order and a
reasonable IDE, navigating large files is a non-issue.

## Consequences

Compile time is starting to become a concern.

## See also

- [A methodical approach to looking at F# compile
  times](https://github.com/dotnet/fsharp/discussions/11134)