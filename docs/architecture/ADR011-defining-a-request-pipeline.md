# ADR011: Defining a request pipeline

## Context

MediatR in C# enables threading the incoming request through a list of methods,
each implementing a cross-cutting concern: JSON serialize the request, time the
request, perform coarse grained authentication, log exceptions, and so on.
Moving cross-cutting concerns to pipeline functions, each application layer
handler doesn't have to be repeat similar code.

In the F# solution this approach isn't as straightforward to implement. The last
pipeline processor must call the appropriate request module's `runAsync`
function. But that's non-trivial as the pipeline processor doesn't have access
to dependencies which must be passed into `runAsync`.

We'd either have to go back to passing to each request the environment or inside
the pipeline partially apply each `runAsync` function. Then we'd need to define
a union of requests, and based on the union case dispatch to the corresponding
`runAsync` function.

Something along these lines of

```fsharp
module RequestPipeline =
    type Request =
        | CreateStory of StoryAggregateRequest.CreateStoryCommand
        | UpdateStory of StoryAggregateRequest.UpdateStoryCommand
        | DeleteStory of StoryAggregateRequest.DeleteStoryCommand
        ...
        
    let run (r: Request) =
        match r with
        | CreateStory r -> StoryAggregateRequest.CreateStoryCommand.runAsync ...
        | UpdateStory r -> StoryAggregateRequest.UpdateStoryCommand.runAsync ...
        | DeleteStory r -> StoryAggregateRequest.DeleteStoryCommand.runAsync ...
```

where `run` would call into a chain of `run` functions (not shown in the code).
In tests, we can still bypass the request pipeline and call `runAsync` functions
directly, allowing us to easily switch out dependencies.

Client code would call `RequestPipeline.run`, passing in the appropriate
request.

MediatR elegantly achieves this behavior by tapping into the .NET dependency
injection (DI) container, which in .NET has effectively become an application
wide service locator.

We'd also have to define a return type of `run` to support every request. No
good return type come to mind, short of copying MediatR interfaces, which isn't
idiomatic F#.

## Decision

In F#, going for a more explicit approach seems more idiomatic, i.e., instead of
a pipeline, duplicate these functions across `runAsync` functions. The
duplication makes it more obvious to readers what's going on.

As an example, `CreateStoryCommand` becomes:

```fsharp
let runAsync
    (stories: IStoryRepository)
    (clock: ISystemClock)
    (logger: ILogger)
    (ct: CancellationToken)
    (cmd: CreateStoryCommand)
    : TaskResult<Guid, CreateStoryHandlerError> =
    let aux () =
        taskResult {
            let! cmd = validate cmd |> Result.mapError ValidationErrors
            do!
                stories.ExistAsync ct cmd.Id
                |> TaskResult.requireFalse (DuplicateStory(StoryId.value cmd.Id))
            let now = clock.CurrentUtc()
            let story, event = StoryAggregate.create cmd.Id cmd.Title cmd.Description now
            do! stories.ApplyEventAsync ct event
            // do! SomeOtherAggregate.Notification.SomeEventHandlerAsync dependencies ct event
            return StoryId.value story.Root.Id
        }

    runWithDecoratorAsync logger (nameof CreateStoryCommand) cmd aux
```

where the decorator is defined as:

```fsharp
let time (fn: unit -> 't) : 't * int =
    let sw = Stopwatch()
    sw.Start()
    let r = fn ()
    r, int sw.ElapsedMilliseconds

let runWithDecoratorAsync (logger: ILogger) (useCase: string) (cmd: 'tcmd) (fn: unit -> 'tresult) : TaskResult<'a, 'b> =
    let result, elapsed =
        time (fun _ ->
            logger.LogRequest useCase cmd
            taskResult { return! fn () })
    logger.LogRequestTime useCase elapsed
    result
```

reusing the decorator across commands and queries, but with explicit invocation
within each `runAsync` function.

## Consequences

Don't attempt to transplant to F# MediatR/object-oriented concepts.
