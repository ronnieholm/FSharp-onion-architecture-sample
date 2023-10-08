# Scrum clean architecture sample

The sample focuses on applying functional constructs over cluing together
libraries and frameworks. It substitutes the .NET dependency injection
container, FluentValidation, MediatR, Entity Framework, Moq, Respawn, and a
migration tool for simpler alternatives.

It's an example of imperative shell, functional core. Specifically, `Program.fs`
and `Infrastructure.fs` make up the shell while `Application.fs` and `Domain.fs`
make up the core.

Where F# shines is in the core and `IntegrationTest.fs`. The shell is similar in
nature to many C# applications.

The application has the following features:

- REST API adhering to the [Zalando API
guidelines](https://opensource.zalando.com/restful-api-guidelines/) with JWTs
supporting role-based security.
- A simple identity provider to issue, renew, and inspect JWTs accepted by
  the service.
- Command Query Responsibility Segregation (CQRS) access to the application
  layer.
- Integration tests with the ability to fake any dependency.
- ASP.NET health checks for memory and database.
- Support for database migrations and data seeding.
- [Architecture Decision
  Records](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
  under `docs/architecuture`.

The Scrum domain is chosen because it offers sufficient complexity and everyone
is familiar with it, though most aspects of the application is illustrated with
stories and tasks only. With only stories and tasks, clean architecture requires
significant support code. With more aggregates, discriminated unions in the
domain, and integrations with external services, the value of the support code
becomes more apparent.

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
curl https://localhost:5000/authentication/renew-token --insecure --request post -H "Authorization: Bearer <token>" | jq
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

## See also

- [Implementing Domain-Driven Design by Vaughn Vernon](https://www.amazon.com/Implementing-Domain-Driven-Design-Vaughn-Vernon/dp/0321834577).
- [Domain Modeling Made Functional: Tackle Software Complexity with Domain-Driven Design and F# by Scott Wlaschin](https://www.amazon.com/Domain-Modeling-Made-Functional-Domain-Driven/dp/1680502549).
- [Jason Taylor's C# Clean Architecture Solution Template](https://github.com/jasontaylordev/CleanArchitecture).
 