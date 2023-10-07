# Scrum clean architecture sample

Focus is on applying functional constructs over cluing together libraries and
frameworks. Instead of the .NET dependency injection container,
FluentValidation, MediatR, Entity Framework, Moq, Respawn, and a migration
tool, this sample implements functional alternatives.

The sample is an imperative shell, functional core with the following features:

- REST API adhering to the [Zalando API
guidelines](https://opensource.zalando.com/restful-api-guidelines/) with JWTs
supporting role-based security.
- A simple identity provider to issue, renew, and inspect JWTs accepted by
  the service.
- Command Query Responsibility Segregation (CQRS) access to the application
  layer.
- Integration tests with the ability to fake any dependency.
- ASP.NET health checks for memory and database.
- Support for database migration and data seeding.
- [Architecture Decision
  Records](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
  under `docs/architecuture`.

The Scrum domain is chosen because it offers sufficient complexity and because
everyone is familiar with it, though most aspects of the application is
illustrated using the concept of stories and tasks only. With only story and
sask, clean architecture requires significant support code. With more
aggregates, discriminated unions in the domain, and integrations with external
services, the support code becomes more apparent.

Where F# shines is in `Domain.fs`, `Application.fs`, and `IntegrationTest.fs`.
As for `Integration.fs` and `Program.fs`, these are similar in nature to many C#
applications.

## Building and testing

Running the tests or the web app automatically creates the SQLite databases.
They're found in the Git root as `scrum_web.sqlite` and `scrum_test.sqlite`.

    $ dotnet build
    $ dotnet test
    $ dotnet run --project src/Scrum

The API supports the following operations:

```bash

# Authentication (supported roles: member and/or admin)
curl "https://localhost:5000/authentication/issue-token?userId=1&roles=member,admin" --insecure --request post | jq
curl https://localhost:5000/authentication/renew --insecure --request post -H "Authorization: Bearer <token>" | jq
curl https://localhost:5000/authentication/introspect --insecure --request post -H "Authorization: Bearer <token>" | jq

# Stories
curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title", "description": "description"}'
curl https://localhost:5000/stories/<storyId> --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}'
curl https://localhost:5000/stories/<storyId>/tasks --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title","description": "description"}'
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}'
curl https://localhost:5000/stories/<storyId> --insecure -H 'Authorization: Bearer <token>' | jq
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request delete -H 'Authorization: Bearer <token>'
curl https://localhost:5000/stories/<storyId> --insecure --request delete -H 'Authorization: Bearer <token>'

# PersistedDomainEvents
curl https://localhost:5000/persisted-domain-events/<aggregateId> --insecure -H 'Authorization: Bearer <token>' | jq

# Health
curl https://localhost:5000/health --insecure | jq
```

## Code organization

Code is organized using horizontal slice architecture:

- Domain.fs
- Application.fs
- Infrastructure.fs
- Program.fs

As file ordering matters to the F# compiler, `Domain.fs` depends on nothing,
`Application.fs` depends on `Domain.fs`, and so on. Similarly, within each
source file definitions must precede use.

For a larger application, the vertical slice architecture may be preferred. Here
`Story.fs` could contain domain, application, infrastructure, web, and possibly
test code (and similar organization for other aggregates):

- Shared.fs
- Story.fs (domain + application + infrastructure + ASP.NET handlers + test)
- Program.fs

Or Story could be a folder with multiple files or its own assembly.

Vertical slice architecture has the potential to improve compile times. The F#
compiler is mostly sequential across an assembly, but multiple assemblies may be
compiled in parallel. For this reasons, separate assemblies for domain,
application, integration, web, unit test and integration test is ill advised.
Compilation would become sequential across the solution.

## See also

- [Implementing Domain-Driven Design by Vaughn Vernon](https://www.amazon.com/Implementing-Domain-Driven-Design-Vaughn-Vernon/dp/0321834577/ref=sr_1_2?crid=1WH0G8B548GPO&keywords=vaughn+vernon&qid=1696680557&sprefix=vaughn+vernon%2Caps%2C306&sr=8-2).
- [Jason Taylor's C# Clean Architecture Solution Template](https://github.com/jasontaylordev/CleanArchitecture).
 