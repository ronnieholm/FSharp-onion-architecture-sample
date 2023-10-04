# Scrum clean architecture sample

Proof of concept F# clean architecture sample.

Focus is on applying functional constructs over cluing together libraries and
frameworks. Instead of .NET dependency injection container, FluentValidation,
MediatR, Entity Framework, Moq, we usee mostly functions.

The sample is an imperative shell, functional core with the following features:

- Command Query Responsibility Segregation (CRQS) entries to application layer.
- REST API adhering to the [Zalando API
guidelines](https://opensource.zalando.com/restful-api-guidelines/) with JWTs
impmenting role-based security.
- A simple identity provider to issue, renew, and inspect JWTs accepted by
  itself.
- Integration tests the abilty to stub out any dependency.
- ASP.NET health checks for memory and database.
- [Architecture Decision
  Records](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
  under `docs/architecuture` covering many trade-off.

The Scrum domain was chosen because it offers sufficient complexity and because
everyone is familiar with it. Most aspects of the application is illustrated
using the concept of stories and tasks only.

The sample also highlights the downside of clean architecture: around the
domain, significant support code is required. Developed once, it can be mostly
reused for the next application.

## Building and testing

If not already present, running the tests or the web app automatically creates
the SQLite databases. They're to be found in the Git root as `scrum_web.sqlite`
and `scrum_test.sqlite`.

    $ dotnet build
    $ dotnet test
    $ dotnet run --project src/Scrum

The API supports the following endpoints:

```bash

# Authentication (supported roles: member and/or admin)
curl "https://localhost:5000/authentication/issueToken?userId=1&role=member,admin" --insecure --request post | jq
curl https://localhost:5000/authentication/renew --insecure --request post -H "Authorization: Bearer <token>" | jq
curl https://localhost:5000/authentication/introspect --insecure --request post -H "Authorization: Bearer <token>" | jq

# Stories
curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title", "description": "description"}'
curl https://localhost:5000/stories/<storyId> --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}'
curl https://localhost:5000/stories/<storyId>/tasks --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title","description": "description"}'
curl https://localhost:5000/stories/<storyId>/tasks/<taskId --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}'
curl https://localhost:5000/stories/<storyId> --insecure -H 'Authorization: Bearer <token>' | jq
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request delete -H 'Authorization: Bearer <token>'
curl https://localhost:5000/stories/<storyId> --insecure --request delete -H 'Authorization: Bearer <token>'

# PersistedDomainEvents
curl https://localhost:5000/persistedDomainEvents/<aggregateId> --insecure -H "Authorization: Bearer <token>" | jq

# Health
curl https://localhost:5000/health --insecure | jq
```

## Code organization

Code is organized using horizontal slice architecture:

- Domain.fs
- Application.fs
- Infrastructure.fs
- Program.fs

As with F# file ordering matterns, it's clear that `Domain.fs` depends on
nothing, `Application.fs` depends on `Domain.fs`, and so on. Within each file
definitions must come before use.

For a larger application, an alternate organization might be along vertical
slices: `Story.fs` would contain domain, application, infrastructure, web, and
possibly test code as well (and similar organization for other aggregates):

- Shared.fs
- Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
- Program.fs

Vertical slice architecture has the potential to improve compile times. The F#
compiler is mostly sequential across an assembly, but multiple assemblies may be
compiled in parallel. For this reasons, separate assemblies for domain,
application, integration, web, unit test integration test is a good idea.
Compilation would become sequantial compilation across the solution.
