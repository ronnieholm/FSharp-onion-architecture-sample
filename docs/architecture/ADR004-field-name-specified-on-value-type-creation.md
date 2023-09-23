# ADR004: Field name specified on value type creating

Status: Accepted and active.

## Context

Besides a descriptive error message, a validation error includes a field name
for correlation. It allows a client to correlate the error with a field in the
user interface.

As such correlation is also relevant to clients, it's maintaned in the
application layer, not the domain layer. As an exmple, if we're calling an
entity's create function from a serializer/deserializer, field name doesn't make
sense. Similarly, if in the domain we're cloning a value object, we don't
necessarily do this based on a client provided field.

## Decision

The pattern for creating value objects becomes:

```fsharp
module GetStoryByIdQuery =
    type GetStoryByIdValidatedQuery = { Id: StoryId }

    let validate (q: GetStoryByIdQuery) : Validation<GetStoryByIdValidatedQuery, ValidationError> =
        validation {
            let! storyId = StoryId.validate q.Id |> Result.mapError (ValidationError.create (nameof q.Id))
            return { Id = storyId }
        }
```

## Consequences

This enables creating a client response with a list of error messages,
correlated to different field, e.g., to show errors next to each field in the
UI.
