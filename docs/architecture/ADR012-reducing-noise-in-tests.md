# ADR012: Reducing noise in tests

## Context

Testing the commands and queries of the Story aggregate translates to calls to
each command and query across the tests. If with every call we have to
explicitly add every dependendency, tests become noisy and hard to follow:

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

By partially applying the commands and queries, we remove most of the noise. We
don't want to partially apply in every test and the partial application code is
identical. So with a setup function for partial application and the test
re-written:

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
functions are exercised by the test. Every time we add a new function, though,
we have to update every test case with an additional underscore. The many
underscores add noise and makes it easy to get the order wrong.

Instead of returning a tuple of functions, returning a record removes the noise
(and the explicitly of which functions the test exercise):

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
        let f = env |> setup
        let cmd = A.createStoryCommand ()
        let! result = f.CreateStory cmd
        test <@ result = Ok(cmd.Id) @>
        let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
        let! result = f.AddTaskToStory cmd
        test <@ result = Ok(cmd.TaskId) @>
        let! result = f.GetStoryById { Id = cmd.StoryId }
        //test <@ result = Ok(_) @>
        do! f.Commit ()
    }              

```

Observe how tests which are assumed to succeed should/must call `Commit` to
update the database or we might miss issues with the database. Tests which are
assumed to fail could similarly call `Rollback`. But by disposing `env` at the
end of each test, before the database reset occurs, `Rollback` is implicitly
called.

## Decision

Returning a record of functions adds an extra layer of indirection to the code,
but reduces noise across tests. Switching out one or more of the dependencies
becomes possible by adding those as arguments to `setup`. Alternatively, we
could instantiate a null `AppEnv` and add in all every dependency.

## Consequences

Less noisy and easy authoring of tests, similating almost any condition.
