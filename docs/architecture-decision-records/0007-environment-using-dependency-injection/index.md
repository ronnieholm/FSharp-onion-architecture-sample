# ADR0007: Environment using dependency injection

Status: Accepted and active.

## Context

The .NET dependency injection (DI) container isn't well suited to the FP nature
of application core. But with core having outer layer dependencies, those most
be made available to it.

Core operates within an application environment (here application refers to
core, not the application as a whole) which mediates access to the world
outside. That the mediation happens through a set of instantiated services is an
implementation detail in the same way that an operating system doesn't access
hardware through dependency injection.

Internally, the environment associates a lifetime to each type of object it
mediates access to:

- Singleton. Every time we access the object, even across requests, it's the
  same instance. Singletons should be avoided as switching it out in one test
  can affect another running in parallel.
- Request. Within the scope of a single request, an instance of the environment,
  we we always access the same instance of the object.
- Transient. Everytime we access the object, it happens through a new instance.

The environment is instantiated in web or the integration tests, providing
ambient access to the world outside core. Specifically, the environment is of
type `AppEnv` which implements `IAppEnv`. `IAppEnv` is then defined in terms of
interfaces from the application layer, implemented in infrastructure and web
layers.

We pass down the environment through the call stack, even though it makes it
less transparent, compared to individual arguments, which parts of the
environment a request handlers is accessing.

The .NET DI container isn't injected into `AppEnv`. Rather, if a dependency
requires a type held by the .NET DI container, such as `IOptions<SomeType>`
configuration, web extract the type from the .NET DI container and passes it
into `AppEnv`.

In some cases, the implementation of an interface is host specific, e.g.,
determining the identity of the user. The challenge becomes how to implement

```fsharp
[<Interface>]
type IScrumIdentity =
    abstract GetCurrent: unit -> ScrumIdentity

```

in a way which supports multiple host environments. For instance, with the
ASP.NET host environment, we use JWTs for identity which requires access to
`HttpContext`. But we can't pass `HttpContext` into `AppEnv` as that would make
it host environment specific.

So we implement `IScrumIdentity` in the web layer (as opposed to the
infrastructure layer, although technically they're both infrastructure), and
pass the instance to `AppEnv`. This way the `ScrumIdentity` implementation can
accept any dependency it needs without affecting the interface. Request handlers
may then make authorization decisions based on the return value of `GetCurrent`.

### Comparison to Wlaschin book

While Wlaschin's example doesn't have an explicit environment, it still has to
combine dependencies (the many functions). It just happens more free floating in
code.

## Decision

We opt for environment over dependency injection and parses `AppEnv` through the
call stack. It makes dependencies a little opaque, but simplifies the code
overall.

## Consequences

Not making use of the .NET DI container is more work upfront, but the benefit is
less complexity overall, including when integrating testing.

## See also

- Based on `docs/di-example.fsx`.
- [Dealing with complex dependency injection in F#](https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f)
- [Six approaches to dependency injection](https://fsharpforfunandprofit.com/posts/dependencies)
- [Composition Root](https://blog.ploeh.dk/2011/07/28/CompositionRoot)