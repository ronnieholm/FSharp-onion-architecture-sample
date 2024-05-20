# ADR0006: Prefer OpenAPI design-first over Swagger

Status: Accepted and active.

## Context

The application doesn't use an auto-generated Swagger (OpenAPI) web interface.
Instead OpenAPI design-first development using a handcrafted specification or
using Stoplight Studio is preferred.

Handcrafting the OpenAPI specification allows the use any part of the OpenAPI
specification. No need to fiddle with Swagger configuration or attributes,
trying to infer how settings affect the specification. Also, no updating the
specification by accident.

Handcrafting doesn't pollute ASP.NET controller and DTO types with a attributes.

## Decision

Handcrafting the specification (with Stoplight Studio) and possibly
code-generating server-side code from the specification, is more robust. As for
testing, the OpenAPI specification can be loaded into Postman which can verify
the service complies with the specification.

## Consequences

Not relying on Swagger implies a greater understanding of what goes into an
OpenAPI specification.
