# ADR005: Functions vs members in type definitions

Status: Accepted and active.

## Context

FSToolkit.ErrorHandling has examples of creating a value object and extracting
its value using members. Our preferred approach, though, is functions in a
module like below:

```fsharp
module StoryTitle =
    let create value =
        match value with
        | v when String.IsNullOrWhiteSpace v -> Error($"Shouldn't by empty or whitespace: '{v}'")
        | v when v.Length > 100 -> Error($"Shouldn't contain more than 100 characters: '{v}'")
        | v -> Ok(StoryTitle v)

    let value (StoryTitle id) = id
```

## Decision

An upside to the member approach is that calls to `Value` become shorter as the
member implicitly references the instance:

```fsharp
task.Title.value 
```

as opposed to

```fsharp
task.Title |> TaskTitle.value
```

A downside to the member approach is that domain types now group data and
functions. It more closely resembles object oriented programming.

## Consequences

We stick with the functional approach as it seems more idiomatic to F#.
