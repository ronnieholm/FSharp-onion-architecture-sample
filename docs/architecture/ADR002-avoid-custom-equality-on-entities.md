# ADR002: Avoid custom equality on entities

Status: Accepted and active.

## Context

An entity's identity is the value of its `Id` field. Therefore we disable
compiler generated `Equals` and `GetHashCode` methods on entities and
aggregates.

## Decision

A less idiomatic approach would be to implement equally per entity like so:

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

Adding custom equality through generic overrides ad ds ceremony. Instead we add
an `equals` function to the entity's module when required:

```fsharp
let equals a b = a.Entity.Id = b.Entity.Id
```

In comparision, with C# object orientation would reduce boilerplate through
inheritance: `Task` would inherit from `Entity` which holds the `Id` field, and
`Equals` and `GetHashCode` could by implemented by the `Entity` base.

## Consequences

To avoid ceremony, we forego the standard. NET equals pattern.

## See also

- https://www.compositional-it.com/news-blog/custom-equality-and-comparison-in-f
- https://fsharpforfunandprofit.com/posts/conciseness-type-definitions
- https://github.com/fsprojects/FSharp.UMX
