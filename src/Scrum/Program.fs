namespace Scrum.Web

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Primitives
open Scrum.Application
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Application.StoryAggregateRequest.AddTaskToStoryCommand
open Scrum.Application.StoryAggregateRequest.CreateStoryCommand
open Scrum.Application.StoryAggregateRequest.DeleteTaskCommand
open Scrum.Application.StoryAggregateRequest.GetStoryByIdQuery
open Scrum.Infrastructure

type Rfc7807Error = { Type: string; Title: string; Status: int; Detail: string }
module Rfc7807Error =
    let create type_ title status detail : Rfc7807Error = { Type = type_; Title = title; Status = status; Detail = detail }

    let internalServerError: Rfc7807Error =
        create "Error" "Error" StatusCodes.Status500InternalServerError "Internal server error"

    let fromValidationError (errors: ValidationError list) : Rfc7807Error =
        let errors = "field error collection goes here"
        create "Error" "Error" StatusCodes.Status400BadRequest errors

    let toJsonResult (acceptHeaders: StringValues) (error: Rfc7807Error) : JsonResult =
        let r = JsonResult(error)
        r.StatusCode <- error.Status

        // Support problem JSON as per https://opensource.zalando.com/restful-api-guidelines/#176.
        let h =
            acceptHeaders.ToArray()
            |> Array.exists (fun v -> v = "application/problem+json")
        r.ContentType <- if h then "application/problem+json" else "application/json"
        r

[<ApiController>]
[<Route("[controller]")>]
type ScrumController() =
    inherit ControllerBase()

    let env = new AppEnv("URI=file:/home/rh/Downloads/scrumfs.sqlite") :> IAppEnv

    member _.Env = env

    interface IDisposable with
        member this.Dispose() = this.Env.Dispose()

type StoryCreateDto = { title: string; description: string }
type AddTaskToStoryDto = { storyId: Guid; title: string; description: string }

[<ApiController>]
[<Route("[controller]")>]
type StoriesController() =
    inherit ScrumController()

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e --insecure --request get

    [<HttpGet>]
    [<Route("{id}")>]
    member x.GetById(id: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let acceptHeaders = x.Request.Headers.Accept
            try
                let! result = StoryAggregateRequest.GetStoryByIdQuery.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                return
                    match result with
                    | Ok s -> OkObjectResult(s) :> ActionResult
                    | Error e ->
                        match e with
                        | ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | StoryNotFound e -> NotFoundResult() // TODO: Search for NotFoundResult for how to include actual Id
            with e ->
                x.Env.Logger.LogException(e)
                return Rfc7807Error.internalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
        }

    // Success: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'
    // Failure: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": ""}'

    [<HttpPost>]
    member x.CreateStory([<FromBody>] request: StoryCreateDto, ct: CancellationToken) : Task<ActionResult> =
        task {
            let acceptHeaders = x.Request.Headers.Accept
            try
                let! result =
                    StoryAggregateRequest.CreateStoryCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                    | Error e ->
                        match e with
                        | CreateStoryCommand.ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | DuplicateStory id -> raise (UnreachableException(string id))
            with e ->
                x.Env.Logger.LogException(e)
                do! x.Env.RollbackAsync(ct)
                return Rfc7807Error.internalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
        }

    // curl https://localhost:5000/stories/fec32101-72b0-4d96-814f-de1c5b2dd140 --insecure --request delete

    [<HttpDelete>]
    [<Route("{id}")>]
    member x.DeleteStory(id: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let acceptHeaders = x.Request.Headers.Accept
            try
                let! result = StoryAggregateRequest.DeleteStoryCommand.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> OkObjectResult(id) :> ActionResult // TODO: status code on delete?
                    | Error e ->
                        match e with
                        | DeleteStoryCommand.ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | DeleteStoryCommand.StoryNotFound e -> NotFoundResult()
            with e ->
                x.Env.Logger.LogException(e)
                do! x.Env.RollbackAsync(ct)
                return Rfc7807Error.internalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
        }

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks/57db7489-722f-4d66-97d5-d5c2501eb89e --insecure --request delete
    
    [<HttpDelete>]
    [<Route("{storyId}/tasks/{taskId}")>]
    member x.DeleteTaskFromStory(storyId: Guid, taskId: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let acceptHeaders = x.Request.Headers.Accept
            try
                // TODO: rename to DeleteTaskFromStoryCommand?
                let! result =
                    StoryAggregateRequest.DeleteTaskCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.Logger
                        ct
                        { StoryId = storyId; TaskId = taskId }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> OkObjectResult(id) :> ActionResult
                    | Error e ->
                        match e with
                        | DeleteTaskCommand.ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | DeleteTaskCommand.StoryNotFound id -> NotFoundResult() :> ActionResult
                        | TaskNotFound id -> NotFoundResult() :> ActionResult
            with e ->
                x.Env.Logger.LogException(e)
                do! x.Env.RollbackAsync(ct)
                return Rfc7807Error.internalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
        }

    // Success: curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'

    [<HttpPost>]
    [<Route("{storyId}/tasks")>]
    member x.AddTaskToStory([<FromBody>] request: AddTaskToStoryDto, storyId: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let acceptHeaders = x.Request.Headers.Accept
            try
                let! result =
                    StoryAggregateRequest.AddTaskToStoryCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> CreatedResult($"/stories/{storyId}/tasks/{id}", id) :> ActionResult
                    | Error e ->
                        match e with
                        | AddTaskToStoryCommand.ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | AddTaskToStoryCommand.StoryNotFound id -> OkResult() :> ActionResult
                        | DuplicateTask id -> raise (UnreachableException(string id))
            with e ->
                x.Env.Logger.LogException(e)
                do! x.Env.RollbackAsync(ct)
                return Rfc7807Error.internalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
        }

type Startup() =
    member _.ConfigureServices(services: IServiceCollection) : unit =
        services.AddMvc(fun options -> options.EnableEndpointRouting <- false) |> ignore
        services.AddControllers() |> ignore
        services.AddResponseCaching() |> ignore
        services.AddEndpointsApiExplorer() |> ignore

    member _.Configure (app: IApplicationBuilder) (env: IWebHostEnvironment) : unit =
        if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore else ()

        app.UseHttpsRedirection() |> ignore
        app.UseResponseCaching() |> ignore
        app.UseRouting() |> ignore
        app.UseMvcWithDefaultRoute() |> ignore

module JsonSerialization =

    ()

module Program =
    let createHostBuilder args : IHostBuilder =
        Host
            .CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(fun builder -> builder.UseStartup<Startup>() |> ignore)

    [<EntryPoint>]
    let main args =
        // Short-hand initialization. .NET 7 moved away from Configure and ConfigureServices, but still support those.
        // - https://learn.microsoft.com/en-us/aspnet/core/migration/50-to-60-samples?view=aspnetcore-7.0
        // - https://mobiletonster.com/blog/code/aspnet-core-6-how-to-deal-with-the-missing-startupcs-file
        //
        // let builder = WebApplication.CreateBuilder(args)
        // builder.Services.AddControllers() |> ignore
        // let app = builder.Build()
        // app.UseHttpsRedirection() |> ignore
        // app.MapControllers() |> ignore
        // app.Run()

        let host = createHostBuilder(args).Build()
        host.Run()
        0
