# ADR007: Dependency injection

## Context

Without the .NET dependency injection (DI) container, we have to manage
dependencies ourselves, explicitly passing dependencies [1] around. Our origin
of dependencies is the `AppEnv` type.

As with the .NET DI container, `AppEnv` supports dependency lifetime:

- Singleton. Everytime we request it, we get back the same instance. Singletons
  should generally be avoided, even for such things as `ISystemClock`, as
  switching it out in one test might affect other tests running in pararallel.
- Request. Within the scope of a single request, an instance of the `AppEnv`, we
  get back the same instance. We achieve this using the F#'s `lazy` type.
- Transient. Everytime we request it, we get back a new instance. The
  implementation is identical to the request lifetime, just without the `lazy`
  part.

The approach used is an adapted version of `di.fsx`.

The idea is to instantiate `AppEnv`, a type which implements `IAppEnv`, whose
members return the dependenicies. `IAppEnv` and interfaces of dependencies are
defined in the application layer and implementated by `AppEnv` in the
infrastructure layer.

Then in web, also part of infrastructure, the `AppEnv`` instance is initialized.
We don't pass it down the call stack as that make dependencies implicit. Instead
in web, we extract relevant depedencies and pass those along explicitly.

We also don't inject the .NET DI container into `AppEnv`. Instead, if a
dependency requires a type held by the .NET DI, such as the database connection
string of and  `IOptions<SomeType>`, web extract those types form the .NET DI
container and passes type into `AppEnv`.

In some cases, the implementation of an interface is host specific, e.g.,
determining the identity of the current user. The challenge is to have

```fsharp
[<Interface>]
type IUserIdentity =
    abstract GetCurrent: unit -> ScrumIdentity

```

remain free of host implementation details while the implementation supports
multiple hosts. With ASP.NET, we might use JWTs for identity which requires
access to `HttpContext`. But we can't pass `HttpContext` into `AppEnv` as that
would make it too host specific.

Instead we implement `UserIdentity` in the web layer (as oppsed to the
infrastructure layer), then pass the instance into `AppEnv`. This way
`UserIdentity` can accept any depedency it needs, such as `HttpContext`, without
affecting the interface. Then request handlers inside the application layer can
make authorization decisions by calling `GetCurrent` and inspecting the roles
property of the identity.

## Decision

The `AppEnv` is a [composition
root](https://blog.ploeh.dk/2011/07/28/CompositionRoot) is an example of a
service locator, albeit not one which permeates the application. So is the .NET
DI container when used with classes not instantiated by framework code.

## Consequences

Not making use of the .NET DI container is more work, but the benefit is less
complexity overall.

[1] https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f
