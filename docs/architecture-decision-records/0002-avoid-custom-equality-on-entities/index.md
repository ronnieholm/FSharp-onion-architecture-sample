# ADR0002: Avoid custom equality on entities

Status: Accepted and active.

## Context

The identity of an entity is the value of its `Id` field only. We therefore
prevent compiler generated `Equals` and `GetHashCode` methods on entities.

A less F# idiomatic approach would be to implement equally like so:

```fsharp
[<NoComparison; NoEquality>]
type Task =
    { Entity: Entity<TaskId>
      Title: TaskTitle
      Description: TaskDescription option }

    interface IEquatable<Task> with
        member x.Equals (other: Task) : bool = other.Entity.Id.Equals x.Entity.Id

    override x.Equals (other: Task) : bool =
        match other with
        | :? Task as t -> (x :> IEquatable<_>).Equals t
        | _ -> false

    override x.GetHashCode() : int = x.Entity.Id.GetHashCode()
```

Adding custom equality through generic overrides is more ceremony than adding an
`equals` function to the entity's module:

```fsharp
let equals a b = a.Entity.Id = b.Entity.Id
```

In the application, not every entity requires testing for equality, so `equals`
could is added ad hoc.

 If we decided to compare `Id`s at call sites, the difference would be

```fsharp
let duplicate = story.Tasks |> List.exists (equals task)
let duplicate = story.Tasks |> List.exists (fun t -> t.Entity.Id = task.Entity.Id)
```

Call sites would require different code depending on if it's an entity or an
aggregate.

## Decision

With C# and object orientation, comparison code would go in an entity base
class, which also holds the `Id` field. In contrast, adding `equals` to entities
which actually require it is a reasonable trade-off for less ceremony.

## Consequences

To avoid ceremony, we forego the standard. NET equals pattern. If we were to
call the code from C#, the behavior might be surprising.

## See also

- https://www.compositional-it.com/news-blog/custom-equality-and-comparison-in-f
- https://fsharpforfunandprofit.com/posts/conciseness-type-definitions
