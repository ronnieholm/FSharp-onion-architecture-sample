# Scrum clean architecture sample

**Work in progress.**

Proof of concept F# clean architecture sample.

Focus is on applying functional constructs over cluing together libraries and
frameworks. Instead of FluentValidation, MediatR, Entity Framework, Moq, we use
functions.

While the template is in F#, many principles are applicable to C#, TypeScript,
and Go as well.

The Scrum domain was chosen because it offers sufficient complexity and because
everyone is familiar with it. Most aspects of the application is illustrated
using the concept of stories and tasks only.

## Random

- An alternate file organization would be along vertical slices. For instance,
  have Story.fs contain domain, application, infrastructure. Although each file
  becomes longer, there's fewer files to care about, and code is easier to read
  top to bottom.

  Example of suggested folder store. Maybe even add xUnit tests in there as
  well. Ship the program with tests or conditionally compile them out.

  Vertical architecture:

  - Shared.fs
  - Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
  - Program.fs

  Horizontal architecture:

  - Domain.fs
  - Application.fs
  - Infrastructure.fs
  - Program.fs

- For this sample, F# compile times compared to C# are bad. A full build of the
  current 0.5 kloc solution takes 21 seconds. To some extend, long compile times
  can be remedied by compiling only the project you work on, e.g., splitting
  each vertical into its own assembly. Compilation of assemblies can happen in
  parallel whereas compiling a single assembly is largely sequential.

## Links

- [Six approaches to dependency injection](https://fsharpforfunandprofit.com/posts/dependencies)
- [Organizing modules in a project](https://fsharpforfunandprofit.com/posts/recipe-part3)
- [Versioning in an Event Sourced System by Gregory Young](https://leanpub.com/esversioning)
- [Architecture Decision Records example](https://github.com/quick-lint/quick-lint-js/tree/master/docs/architecture)
