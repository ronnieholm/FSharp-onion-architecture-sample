# F# onion architecture Scrum sample

*Got a comment or a question? Don't hesitate to drop me an email or open an issue.*

This sample focuses on applying functional constructs over cluing together
libraries and frameworks. It substitutes .NET dependency injection container,
FluentValidation, MediatR, Entity Framework, Moq, Respawn, and a migration tool
for custom code.

It's imperative shell, functional core:

<img src="./docs/onion-architecture.png" width="550px" />

The sample is a modular monolith to offer the simplicity of a monolith and the
scalability of microservices.

It includes the following features:

- Vertical slice architecture, with Story being the only slice.
- REST API adhering to the [Zalando API
guidelines](https://opensource.zalando.com/restful-api-guidelines/) with JWTs
supporting role-based security.
- A simple identity provider to issue, renew, and inspect JWTs accepted by the
  REST API.
- Command Query Responsibility Segregation (CQRS) access to the application
  layer.
- Paged responses for endpoints which return collections.
- Integration tests with the ability to fake any dependency.
- Database migrations and initial seeding.
- ASP.NET health checks for memory and database.
- k6 load test with baseline under `tests/k6`.
- Architecture decision records under `docs/architecture-decision-records`.

## Context

The Scrum domain was chosen because everyone is familiar with it, though most
aspects of the application is illustrated with stories and tasks only. Onion
architecture and domain driven design may therefore appear to introduce a
disproportional amount of complexity. In practice, they're only worth it for
larger, more complex domains.

Not every project requires an implementation of every concept from onion
architecture and domain driven design. Concepts should be scaled up or down
based on business complexity and expected evolution of the application: if core
is expected to only ever be accessed through a web service, code from core
handlers could be moved to HTTP handlers. On the other hand, if core is to be
exposed through multiple of web, gRPC, console, or a long-running service, the
extra indirection with core handlers becomes valuable.

The sample constraints itself to The Blue Book concepts. That means implementing
CQRS, aggregates, entities, events, and so on. For the HTTP API, the sample
adheres to the Zalando API guidelines. It doesn't mean The Blue Book and the
Zalando API guidelines are the end all, be all, but the sample strives to
reflect constraints of a larger real-world application.

## Getting started

Running the tests or the web app creates the SQLite databases in the Git root as
`scrum_web.sqlite` and `scrum_test.sqlite`.

    $ dotnet tool restore
    $ dotnet build
    $ dotnet test
    $ dotnet run --project src/Scrum

Opening the Git repository with VSCode will make it pick up the DevContainer
configuration.

For OpenTelemetry, install and run Jaeger:

    $ docker run --name jaeger -p 16686:16686 -p 4316:4316 -p 4317:4317 -d jaegertracing/opentelemetry-all-in-one

Or run if the image is already installed:

    $ docker start jaeger

The Jaeger web interface is at http://localhost:16686.

## Operations

```bash
# Authentication (supported roles: member and/or admin)
## Post
curl "https://localhost:5000/authentication/issue-token?userId=1&roles=member,admin" --insecure --request POST
curl https://localhost:5000/authentication/renew-token --insecure --request POST --header "Authorization: Bearer <token>"
curl https://localhost:5000/authentication/introspect --insecure --request POST --header "Authorization: Bearer <token>"

# Stories
## Post
curl https://localhost:5000/stories --insecure --request POST --header 'Content-Type: application/json' --header 'Authorization: Bearer <token>' --data '{"title": "title", "description": "description"}'
curl https://localhost:5000/stories/<storyId>/tasks --insecure --request POST --header 'Content-Type: application/json' --header 'Authorization: Bearer <token>' --data '{"title": "title","description": "description"}'

## Put
curl https://localhost:5000/stories/<storyId> --insecure --request PUT --header 'Content-Type: application/json' --header 'Authorization: Bearer <token>' --data '{"title": "title1","description": "description1"}'
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request PUT --header 'Content-Type: application/json' --header 'Authorization: Bearer <token>' --data '{"title": "title1","description": "description1"}'

## Delete
curl https://localhost:5000/stories/<storyId>/tasks/<taskId> --insecure --request DELETE --header 'Authorization: Bearer <token>'
curl https://localhost:5000/stories/<storyId> --insecure --request DELETE --header 'Authorization: Bearer <token>'

## Get
curl https://localhost:5000/stories/<storyId> --insecure --header 'Authorization: Bearer <token>'
curl "https://localhost:5000/stories?limit=<limit>&cursor=<cursor>" --insecure --header 'Authorization: Bearer <token>'

# Events
## Get
curl "https://localhost:5000/events/<aggregateId>?limit=<limit>&cursor=<cursor>" --insecure --header 'Authorization: Bearer <token>'

# Health
## Get
curl https://localhost:5000/health --insecure
```

## Reflections

More so than the classic three-layer architecture, the onion architecture is
good at separating functionality into testable layers. But at the cost of
ceremony:

- Mapping logic is needed with each layer: DTO to/from domain, database to/from
  domain, and value types to wrap primitive types.
- Re-implementation of a request pipeline in Application layer rather than
  re-using what ASP.NET offers out of the box. It enables the Application layer
  to be used across ASP.NET, console, or service hosts.
- Using a document database could alleviate complex, repetitive mapping in
  repositories. For instance
  [PostgreSQL](https://www.postgresql.org/docs/current/functions-json.html) and
  [SQLite](https://sqlite.org/json1.html), supports queries/indices on JSON
  documents, whose documents may represent aggregates, events, or projected read
  models.
- Manually change tracking aggregate state, represented by a document, is
  simpler than change tracking an object graph, not to mention simpler to
  serialize and deserialize without an ORM. Instead of using the database
  transaction log as a replacement for unit of work + identity map, which
  outside SQLite is expensive in terms of round-trips, a unit of work + identity
  map tracking aggregates as a whole is reasonable to implement by hand.
- Storing documents of value types come with their own issues. Value types adds
  another level to the JSON document and require an F# type aware serializer and
  deserializer. Though, the extra level with value types disappear using the
  [UMX](https://github.com/fsprojects/FSharp.UMX) library. Also, migrations may
  require more work compared to SQL scripts. But even with SQL scripts,
  migrating data is the hardest part, regardless of done in a batch or
  on-demand.
- A fully event sourced system looks nice on paper, but in most cases isn't
  worth the hassle. Changes to events over time quickly become non-trivial to
  manage. In many cases, maintaining a persisted domain events table, not used
  to replay state, is good enough for traceability.
- Looking into [Marten](https://martendb.io) and
  [Wolverine](https://wolverinefx.net) may be preferred over custom building
  only what's needed. Their level of documentation or lock-in has to be taken
  into account, though.
- F#'s type system is superior to C#'s, but increased compile times make F# less
  attractive.
- The actor model, implemented by something like
  [Orleans](https://learn.microsoft.com/en-us/dotnet/orleans) might be suited
  for applications where the same aggregate is requested often (as actors are
  stateful) and for serializing updates to aggregates. Each aggregate could
  become an actor with commands and queries becoming actor methods. Inside each
  method would be command/query handler code and Orleans would serve as mediator
  with its request pipeline.
- An actor framework could replay events and keep projections up to date, though
  switching to a document database and a non-actor approach, the stateless
  Application layer may be performant enough.
- An actor framework adds significant complexity. A simpler solution, storing
  aggregate state in memory, may be good enough and easier to reason about.

## See also

- [Implementing Domain-Driven Design by Vaughn Vernon (The Blue Book)](https://www.amazon.com/Implementing-Domain-Driven-Design-Vaughn-Vernon/dp/0321834577).
- [Domain Modeling Made Functional: Tackle Software Complexity with Domain-Driven Design and F# by Scott Wlaschin](https://www.amazon.com/Domain-Modeling-Made-Functional-Domain-Driven/dp/1680502549).
- [Jason Taylor's C# Clean Architecture Solution Template](https://github.com/jasontaylrdev/CleanArchitecture).
- [Uncle Bob: Architecture the Lost Years](https://www.youtube.com/watch?v=WpkDN78P884).
- [.NET Microservices: Architecture for Containerized .NET Applications](https://docs.microsoft.com/en-us/dotnet/architecture/microservices), specifically the chapter on [Tackling Business Complexity in a Microservice with DDD and CQRS Patterns](https://docs.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns).
- [F# units of measure for primitive non-numeric types](https://github.com/fsprojects/FSharp.UMX).
- [Jeremy Miller: Thoughts on Code Organization in a Post-Hexagonal World](https://jeremydmiller.com/2023/08/08/thoughts-on-code-organization-in-a-post-hexagonal-world).
- [Jeremy Miller: Efficient Web Services with Marten V4](https://jeremydmiller.com/2021/09/28/efficient-web-services-with-marten-v4).
- [Jeremy Miller: Why you should give Marten a look before adopting an ORM like EF](https://jeremydmiller.com/2016/09/23/why-you-should-give-marten-a-look-before-adopting-an-orm).
- [Jeremie Chassaing: Functional Event Sourcing Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider).
