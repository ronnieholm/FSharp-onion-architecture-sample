# ADR006: Prefer OpenAPI design-first over Swagger

Status: Accepted and active.

## Context

The application doesn't rely on the auto-generated Swagger (OpenAPI) web
interface. Instead OpenAPI design-first development using a handcrafted
specification or crafting using Stoplight Studio is preferred.

Handcrafting the OpenAPI specification allows us to use any part of the OpenAPI
specification. No need to fiddle with Swagger configuration or attributes,
trying to infer how settings affect the specification. And no updating the
specification by accident.

Handcrafting also doesn't pollute ASP.NET controller and DTO types with a
attributes.

## Decision

Handcrafting the specification (with Stoplight Studio) and possibly
code-generating server-side code from the specification, is more robust. As for
testing, the OpenAPI specification can be loaded into Postman which can generate
stub calls and verify that the service complies with the specification.

## Consequences

Not relying on Swagger implies a greater understanding of what goes into an
OpenAPI specification and how it's structured.
