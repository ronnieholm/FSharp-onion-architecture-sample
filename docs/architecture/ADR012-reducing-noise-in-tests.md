# ADR012: Reducing noise in tests

Status: Accepted and active.

## Context

Testing the Story aggregate translates to calling commands and queries across
tests. If with every call we have to explicitly add every dependency, tests
become noisy and hard to follow:

```fsharp
[<Fact>]
let ``create story with task`` () =        
    task {
        let env = new AppEnv(connectionString) :> IAppEnv
        let cmd = A.createStoryCommand ()
        let! result = CreateStoryCommand.runAsync env.StoryRepository env.SystemClock env.Logger CancellationToken.None cmd
        test <@ result = Ok(cmd.Id) @>
        let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
        let! result = AddTaskToStoryCommand.runAsync env.StoryRepository env.SystemClock env.Logger CancellationToken.None cmd
        test <@ result = Ok(cmd.TaskId) @>
        let! result = GetStoryByIdQuery.runAsync env.StoryRepository env.Logger CancellationToken.None { Id = cmd.StoryId }
        //test <@ result = Ok(_) @>
        do! env.CommitAsync CancellationToken.None            
    }
```

Partially applying commands and queries, we can remove most of the noise. We
don't want to partially apply in every test as the partial application code is
identical. So with a setup function for partial application, the test becomes:

```fsharp
let setup (env: IAppEnv) =
    let r = env.StoryRepository
    let s = env.SystemClock
    let l = env.Logger
    let ct = CancellationToken.None
    CreateStoryCommand.runAsync r s l ct,
    AddTaskToStoryCommand.runAsync r s l ct,
    GetStoryByIdQuery.runAsync r l ct,
    DeleteStoryCommand.runAsync r l ct,
    DeleteTaskCommand.runAsync r l ct,
    UpdateStoryCommand.runAsync r s l ct,
    UpdateTaskCommand.runAsync r s l ct,
    fun _ -> env.CommitAsync ct

[<Fact>]
let ``create story with task`` () =
    use env = new AppEnv(connectionString)
    task {
        let createStory, addTaskToStory, getStoryById, _, _, _, _, commit = env |> setup
        let cmd = A.createStoryCommand ()
        let! result = createStory cmd
        test <@ result = Ok(cmd.Id) @>
        let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
        let! result = addTaskToStory cmd
        test <@ result = Ok(cmd.TaskId) @>
        let! result = getStoryById { Id = cmd.StoryId }
        //test <@ result = Ok(_) @>
        do! commit ()
    }    
```

Returning a tuple of partially applied functions makes it obvious which
functions are exercised in the test. Every time we add a new function, though,
we have to update every test with an additional underscore. Also, the many
underscores add noise and it's easy to get the order wrong.

Instead of returning a tuple of functions, returning an anonymous record removes
the noise but makes it implicit which functions the test exercise:

```fsharp
let setup (env: IAppEnv) =
    let r = env.StoryRepository
    let s = env.SystemClock
    let l = env.Logger
    let ct = CancellationToken.None        
    {| CreateStory = CreateStoryCommand.runAsync r s l ct
       AddTaskToStory = AddTaskToStoryCommand.runAsync r s l ct
       GetStoryById = GetStoryByIdQuery.runAsync r l ct
       DeleteStory = DeleteStoryCommand.runAsync r l ct
       DeleteTask = DeleteTaskCommand.runAsync r l ct
       UpdateStory = UpdateStoryCommand.runAsync r s l ct
       UpdateTask = UpdateTaskCommand.runAsync r s l ct
       Commit = fun _ -> env.CommitAsync ct |}  

let ``create story with task`` () =
    use env = new AppEnv(connectionString)
    task {
        let fns = env |> setup
        let cmd = A.createStoryCommand ()
        let! result = fns.CreateStory cmd
        test <@ result = Ok(cmd.Id) @>
        let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
        let! result = fns.AddTaskToStory cmd
        test <@ result = Ok(cmd.TaskId) @>
        let! result = fns.GetStoryById { Id = cmd.StoryId }
        //test <@ result = Ok(_) @>
        do! f.Commit ()
    }              

```

With this approach, observe how tests which are assumed to succeed should/must
call `Commit` to update the database or we might miss issues with the database.
Tests which are assumed to fail could similarly call `Rollback`. But by
disposing `env` at the end of each test, `Rollback` is called implicitly.

## Decision

Returning a record of functions adds an extra layer of indirection to the code,
but reduces noise across tests. Switching out one or more dependencies becomes
simple (see `IntegrationTests.fs`). Alternatively, we could instantiate a null
`AppEnv` and require adding in all every dependency for a test.

## Consequences

Less noisy and easy authoring of tests, simulating almost any condition with
business logic and external dependencies.
