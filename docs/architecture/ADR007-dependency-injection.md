# ADR007: Dependency injection

## Context

Without a dependency injection container, we must manage dependencies ourselves,
explicitly passing dependencies [1].

Dependencies can be grouped by lifetime:

- Singleton. Everytime we request it, we get back the same instance.
- Request. Within the scope of a single request, we get back the same instance.
- Transient. Everytime we request it, we get back a new instance.

## Decision

The approach used is an adapted version of `di.fsx`.

The idea is to instantiate a type based on `IAppEnv` whose members return
singleton, request, and transient scoped instances. Required interfaces are
defined in application and implementated in `AppEnv` in Infrastructure.

Then in web, part of infrastructure, the AppEnv instance is initialized and
passed down the call stack.

Sometimes we want to take advantage of the .NET DI, for instance where a .NET
package is making use of it or for re-using HttpClients. For these cases, we
inject the DI container into the `AppEnv` where it can be used as a service
locator in constructing types exposed by `AppEnv`.

## Consequences

While we can now pass required dependencies using a single AppEnv argument,
function signatures no longer indicate it's dependencies.

[1] https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f
