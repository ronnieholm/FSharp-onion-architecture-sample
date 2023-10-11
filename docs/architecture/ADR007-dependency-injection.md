# ADR007: Dependency injection

Status: Accepted and active.

## Context

An object oriented dependency injection (DI) container isn't particularly well
suited to FP code. Going without the .NET DI container, we have to manage
dependencies in another way. When needed, dependencies are explicitly passed
around and functions partially evaluated.

Our source of dependencies is the `AppEnv` type.

As with the .NET DI container, `AppEnv` supports dependency lifetime:

- Singleton. Everytime we the dependency, we get back the same instance.
  Singletons should generally be avoided, even for such things as
  `ISystemClock`, as switching it out in one test can affect other tests running
  in parallel.
- Request. Within the scope of a single request, an instance of the `AppEnv`, we
  get back the same dependency instance. To achieve this, we make use of F#'s
  lazy evaluation.
- Transient. Everytime we request the dependency, we get back a new instance.
  The implementation is identical to that of the request lifetime, except
  without the lazy part.

The AppEnv approach used is an adapted version of `di.fsx`.

The idea is to instantiate `AppEnv`, a type which implements `IAppEnv`, whose
members return the dependencies. `IAppEnv` and interfaces are defined in the
application layer and implemented by `AppEnv` in the infrastructure layer.

Then in web, also part of infrastructure, the `AppEnv` instance is initialized.
By design, we don't pass it down the call stack as that makes dependencies
implicit. Instead in web (inside controller actions), we extract relevant
dependencies fro the `AppEnv` instance and pass explicitly pass those into
request handlers (the `runAsync` functions).

We don't inject the .NET DI container into `AppEnv`. Rather, if a dependency
requires a type held by the .NET DI, such as the database connection string or
`IOptions<SomeType>` configuration data, web extract these types from the .NET
DI container and passes those into `AppEnv`.

In some cases, the implementation of an interface is host specific, e.g.,
determining the identity of the current user. The challenge then becomes how to
implement

```fsharp
[<Interface>]
type IUserIdentity =
    abstract GetCurrent: unit -> ScrumIdentity

```

in a way which supports multiple hosts. With ASP.NET, we use JWTs for identity
which requires access to `HttpContext`. But we can't pass `HttpContext` into
`AppEnv` as that would make it host specific.

Instead we implement `UserIdentity` in the web layer (as opposed to the
infrastructure layer), and pass the constructed instance to `AppEnv`. This way
`UserIdentity` can accept any dependency it needs without affecting the
interface. Request handlers inside the application layer then make authorization
decisions based on `GetCurrent`.

### Comparison to Wlaschin book

One could make the argument that passing `IStoryRepository` instead of only the
needed functions we're still not explicit enough about dependencies. But passing
individual functions leads to more arguments. Passing individual interface
implementations is the middle ground between a single `IAppEnv` argument and
individual functions (but perhaps we should backtrack and pass `IAppEnv`.
Handlers aren't that long anyway and one can extract a list of depedencies by
searching handler code for `env.`).

While Wlaschin doesn't have an explicit `appEnv`, he still has to combine
depedencies (the many functions). It just so happens more free floating in his
code.

## Decision

The `AppEnv` is a [composition
root](https://blog.ploeh.dk/2011/07/28/CompositionRoot) and an example of a
service locator, albeit not one which permeates the layers. So too is the .NET
DI container when used explicitly within classes not instantiated by framework
code.

## Consequences

Not making use of the .NET DI container is more work upfront, but the benefit is
less complexity overall, including with integrating testing.

## See also

- [Dealing with complex dependency injection in F#](https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f)
- [Six approaches to dependency injection](https://fsharpforfunandprofit.com/posts/dependencies)