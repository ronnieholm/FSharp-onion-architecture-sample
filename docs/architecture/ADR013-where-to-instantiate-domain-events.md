# ADR013: where to instantiate domain events

Status: Accepted and active.

## Context

While domain events are defined in the domain, they don't have to be
instantiated there. Instantiating from the domain, though, has the benefit that
changes to the entity and the associated domain event are right next to each
other:

```fsharp
[<NoComparison; NoEquality>]
type Story =
    { Aggregate: AggregateRoot<StoryId>
      Title: StoryTitle
      Description: StoryDescription option
      Tasks: TaskEntity.Task list }

type StoryCreated =
    { DomainEvent: DomainEvent
      StoryId: StoryId
      StoryTitle: StoryTitle
      StoryDescription: StoryDescription option }

type StoryDomainEvent =
    | StoryCreated of StoryCreated

let create (id: StoryId) (title: StoryTitle) (description: StoryDescription option) (occurredAt: DateTime) : Story * StoryDomainEvent =
    { Aggregate = { Id = id; CreatedAt = occurredAt; UpdatedAt = None }
        Title = title
        Description = description
        Tasks = [] },
    StoryDomainEvent.StoryCreated(
        { DomainEvent = { OccurredAt = occurredAt }
          StoryId = id
          StoryTitle = title
          StoryDescription = description }
    )
```

The downside is that every time we create a `Story` the event is created, too.
This isn't so much an issue if `create` is only called from
`CreateStoryCommand.runAsync`. But what if we also wanted to call `create`
during database deserialization? We could ignore the returned domain event, but
it feels inelegant.

We consider deserialization to work on unvalidated input. So we want it to go
through the `create` functions on value objects and entities. Even though we
previously serialized the data and wrote to it to the database, data could've
changed between then and until we the next read, e.g., by a database migration
script. If we treat the database as validated input, skipping `create`
functions, it can leave the domain in a invalid starting state. Now in database
deserialization logic, if validation fails, the code should panic.

In the example above, `create` isn't doing any inter-field validation, but in
other cases invariants might be present. Those checks we shouldn't bypass,
setting Story fields directly.

The other reasonable place to instantiate domain events is in the command
handler. It has the benefit that the `occuredAt` parameter above becomes a clear
`createdAt` or `updatedAt`, and we don't mix up using the same timestamp for
entity fields and domain event fields.

After moving domain event instantiation to the handler, it looks like so:

```fsharp
let runAsync
    (identity: IUserIdentity)
    (stories: IStoryRepository)
    (clock: ISystemClock)
    (logger: ILogger)
    (ct: CancellationToken)
    (cmd: CreateStoryCommand)
    : TaskResult<Guid, CreateStoryError> =
    let aux () =
        taskResult {
            do! isInRole identity Member |> Result.mapError AuthorizationError
            let! cmd = validate cmd |> Result.mapError ValidationErrors
            do!
                stories.ExistAsync ct cmd.Id
                |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
            let story =
                StoryAggregate.create cmd.Id cmd.Title cmd.Description [] (clock.CurrentUtc())
            let event =
                StoryDomainEvent.StoryCreated(
                    { DomainEvent = { OccurredAt = (clock.CurrentUtc()) }
                      StoryId = story.Aggregate.Id
                      StoryTitle = story.Title
                      StoryDescription = story.Description }
                )
            do! stories.ApplyEventAsync ct event
            // Example of publishing the StoryCreated domain event to
            // another aggregate:
            // do! SomeOtherAggregate.SomeEventNotificationAsync dependencies ct event
            // Integration events may be generated here and persisted.
            return StoryId.value story.Aggregate.Id
        }

    runWithDecoratorAsync logger (nameof CreateStoryCommand) cmd aux
```

Now domain event and potential integration event instantiation are placed next
to each other in code.

Looking at C# DDD examples, they typically instantiate domain events inside
entity operations. The reason for this is that domain events tend to be added to
a collection in the Aggregate base class. During EF save, this collection of
modified aggregate is iterated for publishing.

## Decision

To separate domain operations from domain event instantiation, the latter should
take place in the handler.

## Consequences

Assigns yet another coordinating responsibility to the handler.
