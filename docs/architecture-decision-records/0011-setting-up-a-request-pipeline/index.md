# ADR0011: Setting up a request pipeline

Status: Accepted and active.

## Context

MediatR supports threading the incoming request through middleware, each
responsible for a cross-cutting concern: JSON serialize the request to a log,
time the request from start to finish, perform coarse grained authentication,
log unhalted exceptions, and so on.

Moving these cross-cutting concerns to a pipeline functions means each
application layer handler doesn't have to repeat the same code.

## Decision

In F#, going for a more explicit approach is more idiomatic, i.e., instead of a
pipeline, have each handler call `runWithDecoratorAsync`.

As an example, `CaptureBasicStoryDetailsCommand` becomes:

```fsharp
let runAsync (env: IAppEnv) (ct: CancellationToken) (cmd: CaptureBasicStoryDetailsCommand) : TaskResult<Guid, CaptureBasicStoryDetailsCommand> =
    let aux () =
        taskResult {
            do! isInRole env.Identity Member |> Result.mapError AuthorizationError
            let! cmd = validate cmd |> Result.mapError ValidationErrors
            do!
                env.Stories.ExistAsync ct cmd.Id
                |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
            let! story, event =
                StoryAggregate.captureBasicStoryDetails cmd.Id cmd.Title cmd.Description [] (env.Clock.CurrentUtc()) None
                |> Result.mapError fromDomainError
            do! env.Stories.ApplyEventAsync ct event
            return StoryId.value story.Aggregate.Id
        }

    runWithDecoratorAsync env.Logger (nameof CaptureStoryBasicDetailsCommand) cmd aux
```

where the decorator is defined as:

```fsharp
let time (fn: unit -> 't) : 't * uint<ms> =
    let sw = Stopwatch()
    sw.Start()
    let result = fn ()
    let elapsed = (uint sw.ElapsedMilliseconds) * 1u<ms>
    result, elapsed

let runWithDecoratorAsync (logger: IScrumLogger) (useCase: string) (cmd: 't) (fn: unit -> TaskResult<'a, 'b>) : TaskResult<'a, 'b> =
    let result, elapsed =
        time (fun _ ->
            logger.LogRequestPayload useCase cmd
            taskResult { return! fn () })
    // Don't log errors from evaluating fn as these are expected errors. We
    // don't want those to pollute the log with.
    logger.LogRequestDuration useCase elapsed
    result
```

The same decorator can be reused across commands and queries.

## Consequences

Avoid transplanting to F# MediatR/object oriented concepts when oftentimes a few
functions can achieve the same effect.
