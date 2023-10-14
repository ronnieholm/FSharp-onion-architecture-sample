# Scrum clean architecture sample

*Got a comment or a question? Don't hesitate to drop me an email or open an issue.*

This sample focuses on applying functional constructs over cluing together
libraries and frameworks. It substitutes the .NET dependency injection
container, FluentValidation, MediatR, Entity Framework, Moq, Respawn, and a
migration tool for simpler constructs.

It's an example of imperative shell, functional core. Specifically, `Program.fs`
and `Infrastructure.fs` make up the shell while `Application.fs` and `Domain.fs`
make up the core.

Where F# shines is in the core and `IntegrationTest.fs`. The shell is similar in
nature to many C# applications.

<img src="./docs/onion-architecture.png" width="550px" />

The application has the following features:

- REST API adhering to the [Zalando API
guidelines](https://opensource.zalando.com/restful-api-guidelines/) with JWTs
supporting role-based security.
- A simple identity provider to issue, renew, and inspect JWTs accepted by the
  REST API.
- Command Query Responsibility Segregation (CQRS) access to the application
  layer from clients.
- Paged responses for endpoints which return collections.
- Integration tests with the ability to fake any dependency.
- Database migrations and initial data seeding.
- ASP.NET health checks for memory and database.
- Architecture decision records under `docs/architecture-decision-records`.

The Scrum domain was chosen because it offers sufficient complexity and everyone
is familiar with it, though most aspects of the application is illustrated with
stories and tasks only.

With only stories and tasks, clean architecture may seem to introduce a
disproportional amount of complexity. A larger domain and integrations with
external services is where clean architecture starts to pays off.

That said, not every project requires an implementation of every concept from
clean architecture and domain driven design. Those should be scaled up or down
based on actual business complexity.

## Getting started

Running the tests or the web app creates the SQLite databases in the Git root as
`scrum_web.sqlite` and `scrum_test.sqlite`.

    $ dotnet tool restore
    $ dotnet build
    $ dotnet test
    $ dotnet run --project src/Scrum

Opening the Git repository with VSCode will make it pick up the DevContainer
configuration.

## Operations

```bash
# Authentication (supported roles: member and/or admin)
## Post
curl "https://localhost:5000/authentication/issue-token?userId=1&roles=member,admin" --insecure --request post | jq
curl https://localhost:5000/authentication/renew-token --insecure --request post -H "Authorization: Bearer <token>" | jq
curl https://localhost:5000/authentication/introspect --insecure --request post -H "Authorization: Bearer <token>" | jq

# Stories
## Post
curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title", "description": "description"}' | jq
curl https://localhost:5000/stories/<storyId>/tasks --insecure --request post -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title","description": "description"}' | jq

## Put
curl https://localhost:5000/stories/<storyId> --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}' | jq
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request put -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{"title": "title1","description": "description1"}' | jq

## Delete
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request delete -H 'Authorization: Bearer <token>' | jq
curl https://localhost:5000/stories/<storyId> --insecure --request delete -H 'Authorization: Bearer <token>' | jq

## Get
curl https://localhost:5000/stories/<storyId> --insecure -H 'Authorization: Bearer <token>' | jq
curl "https://localhost:5000/stories?limit=<limit>&cursor=<cursor>" --insecure -H 'Authorization: Bearer <token>' | jq

# PersistedDomainEvents
## Get
curl https://localhost:5000/persisted-domain-events/<aggregateId> --insecure -H 'Authorization: Bearer <token>' | jq

# Health
## Get
curl https://localhost:5000/health --insecure | jq
```

## See also

- [Implementing Domain-Driven Design by Vaughn Vernon](https://www.amazon.com/Implementing-Domain-Driven-Design-Vaughn-Vernon/dp/0321834577).
- [Domain Modeling Made Functional: Tackle Software Complexity with Domain-Driven Design and F# by Scott Wlaschin](https://www.amazon.com/Domain-Modeling-Made-Functional-Domain-Driven/dp/1680502549).
- [Jason Taylor's C# Clean Architecture Solution Template](https://github.com/jasontaylrdev/CleanArchitecture).
- [Uncle Bob: Architecture the Lost Years](https://www.youtube.com/watch?v=WpkDN78P884).
- [.NET Microservices: Architecture for Containerized .NET Applications](https://docs.microsoft.com/en-us/dotnet/architecture/microservices), specifically the chapter on [Tackling Business Complexity in a Microservice with DDD and CQRS Patterns](https://docs.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns).
