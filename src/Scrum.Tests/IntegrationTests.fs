namespace Scrum.Tests

open System
open System.Threading
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Infrastructure
open Swensen.Unquote
open Xunit
open System.Data.SQLite

// TODO: Organize tests into modules (command, query)
// TODO: How to clear database between runs? No need to use typical .NET library, just issue delete * table statements in test class dispose method.

module A =
    let createStoryCommand () : CreateStoryCommand = { Id = Guid.NewGuid(); Title = "title"; Description = Some "description" }

    let updateStoryCommand (source: CreateStoryCommand) = { Id = source.Id; Title = source.Title; Description = source.Description }

    let addTaskToStoryCommand () : AddTaskToStoryCommand =
        { TaskId = Guid.NewGuid()
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

    let updateTaskCommand (cmd: AddTaskToStoryCommand) =
        { StoryId = cmd.StoryId
          TaskId = cmd.TaskId
          Title = cmd.Title
          Description = cmd.Description }

[<CollectionDefinition(nameof DisableParallelization, DisableParallelization = true)>]
type DisableParallelization() =
    class
    end

// Serializing integration tests makes for slower but more reliable test runs. With SQLite, only one
// transaction can be in progress at once anyway. Another transaction will block on commit until the
// ongoing transaction finish commit or rollback. Thus commenting out the attribute below likely
// results in tests succeeding. But if any test assume a reset database, tests may start failing.
// In order for tests not to interfere with each other and the reset, take must be taken to serialize
// test runs. 
[<Collection(nameof DisableParallelization)>]
type StoryAggregateRequestTests() =
    let connectionString = "URI=file:/home/rh/Downloads/scrumfs.sqlite"

    let missing () = Guid.NewGuid()

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
           Commit = fun _ -> env.CommitAsync ct
           Rollback = fun _ -> env.RollbackAsync ct |}  
    
    // TODO: move function out of any specific test as it clean all tables
    let resetDatabase () =
        // Run SQL statements in reverse dependency order.
        let sql = [| "delete from tasks"; "delete from stories" |]
        use connection = new SQLiteConnection(connectionString)
        connection.Open()
        let transaction = connection.BeginTransaction()
        sql
        |> Array.iter (fun sql ->
            use cmd = new SQLiteCommand(sql, connection, transaction)
            // TODO: use fold to accumulate records affected for debugging
            cmd.ExecuteNonQuery() |> ignore)
        transaction.Commit()
        
    [<Fact>]        
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

    [<Fact>]
    let ``create duplicate story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let! result = f.CreateStory cmd
            test <@ result = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story without tasks`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let! result = f.DeleteStory { Id = cmd.Id }
            test <@ result = Ok(cmd.Id) @>
            let! result = f.GetStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story with task`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = f.AddTaskToStory cmd
            let! result = f.DeleteStory { Id = cmd.StoryId }
            test <@ result = Ok(cmd.StoryId) @>
            let! result = f.GetStoryById { Id = cmd.StoryId }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let createStoryCmd = A.createStoryCommand ()
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = f.CreateStory createStoryCmd
            let! _ = f.AddTaskToStory addTaskCmd
            let! result = f.AddTaskToStory addTaskCmd
            test <@ result = Error(AddTaskToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = { A.addTaskToStoryCommand () with StoryId = missing () }
            let! result = f.AddTaskToStory cmd
            test <@ result = Error(AddTaskToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete existing task on story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = f.AddTaskToStory cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! result = f.DeleteTask cmd
            test <@ result = Ok(cmd.TaskId) @>
            do! f.Commit ()
        }

    [<Fact>]
    let ``delete task on non-existing story`` () =
        use env = new AppEnv(connectionString)        
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let cmd = { StoryId = missing (); TaskId = cmd.TaskId }
            let! result = f.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete non-existing task on story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { StoryId = cmd.Id; TaskId = missing () }
            let! result = f.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update existing story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = A.updateStoryCommand cmd
            let! result = f.UpdateStory cmd
            test <@ result = Ok(cmd.Id) @>
            do! f.Commit ()
        }

    [<Fact>]
    let ``update non-existing story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let cmd = A.updateStoryCommand cmd
            let! result = f.UpdateStory cmd
            test <@ result = Error(UpdateStoryCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``update existing task`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = f.AddTaskToStory cmd
            let cmd = A.updateTaskCommand cmd
            let! result = f.UpdateTask cmd
            test <@ result = Ok(cmd.TaskId) @>
            do! f.Commit ()
        }

    [<Fact>]
    let ``update non-existing task on existing story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = f.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with TaskId = missing () }
            let! result = f.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update task on non-existing story`` () =
        use env = new AppEnv(connectionString)
        task {
            let f = env |> setup
            let cmd = A.createStoryCommand ()
            let! _ = f.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = f.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with StoryId = missing () }
            let! result = f.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    interface IDisposable with
        member _.Dispose() = resetDatabase ()
