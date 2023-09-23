# ADR002: Avoid custom equality on entities

Status: Accepted and active.

## Context

An entity's identity is the value of its `Id` field. Therefore we prevent
compiler generated `Equals` and `GetHashCode` methods.

## Decision

A less idiomatic approach would be implementing equally per entity like so:

```fsharp
[<NoComparison; NoEquality>]
type Task =
    { Id: TaskId
      Title: TaskTitle
      Description: TaskDescription option }
                           
    interface IEquatable<Task> with
        member this.Equals other = other.Id.Equals this.Id
        
    override this.Equals other =
        match other with
        | :? Task as t -> (this :> IEquatable<_>).Equals t
        | _ -> false
        
    override this.GetHashCode () = this.Id.GetHashCode()
```

Adding custom equality through [generic](1) [overrides](2) adds ceremony.
Instead we add an `equals` function to the entity's module when required:

```fsharp
let equals a b = a.Entity.Id = b.Entity.Id
```

Object orientation would reduce boilerplate through inheritance: `Task` would
inherit from `Entity` which holds the `Id` field, and `Equals` and `GetHashCode`
would by implemented by the `Entity` base.

## Consequences

Adding `Equals` and `GetHashCode` at the module level isn't supported by F#. To
avoid ceremony, we forego the standard. NET equals pattern. This may have
unknown adverse effect down the line.

[1] https://www.compositional-it.com/news-blog/custom-equality-and-comparison-in-f
[2] https://fsharpforfunandprofit.com/posts/conciseness-type-definitions
