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

For larger applications, switching to vertical slice architecture is preferred
as related code is typically read or modified together rather than someone
reading domain or application top to buttom. With vertical slice architecture,
`Story.fs` (or a variation thereof) would contain domain, application,
infrastructure, web:

- Shared.fs
- Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
- Program.fs

`Story.fs` could also be a folder with multiple files or its own assembly for
faster fan-out compile times across slices. The F# compiler is mostly sequential
across an assembly, so slices depending on each other would make compilation
sequential.

## Decision

To illustrate the concepts as if it was a larger application, we use vertical
slice architecture with one F# file per vertical slice. It makes files larger,
but keeps code changing together next to each other. Roughly speaking, each
module within such file corresponds to a class in C# (and nested modules to
subfolders). With types defined in dependency order and a reasonable IDE,
navigating large files is a non-issue.

## Consequences

Compile time is starting to become a concern with sequential ordering of layers
in horizontal architecture.

## See also

- [A methodical approach to looking at F# compile
  times](https://github.com/dotnet/fsharp/discussions/11134)