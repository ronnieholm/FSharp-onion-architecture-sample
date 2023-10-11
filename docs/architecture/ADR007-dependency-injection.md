# ADR007: Dependency injection

Status: Accepted and active.

## Context

The .NET dependency injection (DI) container isn't well suited to core's FP
approach. But core still has outer layer dependencies, so we have to inject
those in another way.

Core operates within an application environment (here application refers to
core, not the application as a whole) which provides controlled access to the
world outside code. As with the .NET DI container, the environment supports
lifetimes transparent to the application:

- Singleton. Everytime we request the dependency, we get back the same instance.
  Singletons should be avoided, even for `IClock`, as switching out the clock in
  one test can affect other tests running in parallel.
- Request. Within the scope of a single request, an instance of the environment,
  we get back the same dependency instance.
- Transient. Everytime we request the dependency, we get back a new instance.

The environment approach is an adapted version of `di.fsx`.

The idea is to instantiate `AppEnv` in web or the integration tests. Its members
then provide access to the world outside core. `AppEnv` implements `IAppEnv`
whose interfaces are defined in the application layer and implemented by
infrastructure and web layers.

We pass down the `AppEnv` instance through the call stack, even though it makes
it less transparent which parts of the environment request handlers are
accessing. It's still easy to discover, though, by searching for "env." without
a handler.

The .NET DI container isn't injected into `AppEnv`. Rather, if a dependency
requires a type held by the .NET DI, such as the database connection string or
generally `IOptions<SomeType>` configuration data, web extract these types from
the .NET DI container and passes those into `AppEnv`.

In some cases, the implementation of an interface is host specific, e.g.,
determining the identity of the current user. The challenge then becomes how to
implement

```fsharp
[<Interface>]
type IUserIdentity =
    abstract GetCurrent: unit -> ScrumIdentity

```

in a way which supports multiple host environments. With ASP.NET, we use JWTs
for identity which requires access to `HttpContext`. But we can't pass
`HttpContext` into `AppEnv` as that would make it host environment specific.

Instead we implement `UserIdentity` in the web layer (as opposed to the
infrastructure layer, although technically they're both infrastructure), and
pass the instance to `AppEnv`. This way `UserIdentity` can accept any dependency
it needs without affecting the interface. Request handlers may then make
authorization decisions based on `GetCurrent`.

Technically the environment is dependency injection -- it forms a [composition
root](https://blog.ploeh.dk/2011/07/28/CompositionRoot) -- through a service
locator. Conceptually, the environment is a mediator between core and the
outside world. That mediation happens though a set of instantiated services is
an implementation detail.

### Comparison to Wlaschin book

While Wlaschin's example doesn't have an explicit environment, it still has to
combine depedencies (the many functions). It just happens more free floating in
code.

## Decision

Opt for parsing `AppEnv` through the call stack. It makes dependencies a little
opaque, but simplifies the code overall.

## Consequences

Not making use of the .NET DI container is more work upfront, but the benefit is
less complexity overall, including with integrating testing.

## See also

- [Dealing with complex dependency injection in F#](https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f)
- [Six approaches to dependency injection](https://fsharpforfunandprofit.com/posts/dependencies)