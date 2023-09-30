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
open Scrum.Application.StoryAggregateRequest.CreateStoryCommand
open Scrum.Infrastructure

type Rfc7807Error = { Type: string; Title: string; Status: int; Detail: string }
module Rfc7807Error =
    let create type_ title status detail : Rfc7807Error = { Type = type_; Title = title; Status = status; Detail = detail }

    let createInternalServerError: Rfc7807Error =
        create "Error" "Error" StatusCodes.Status500InternalServerError "Internal server error"

    let fromValidationError (errors: ValidationError list) : Rfc7807Error =
        let errors = "field error collection goes here"
        create "Error" "Error" StatusCodes.Status400BadRequest errors

    let toJsonResult (acceptHeaders: StringValues) (error: Rfc7807Error) : JsonResult =
        let r = JsonResult(error)
        r.StatusCode <- error.Status

        // Must support problem JSON as per https://opensource.zalando.com/restful-api-guidelines/#176
        let h =
            acceptHeaders.ToArray()
            |> Array.exists (fun v -> v = "application/problem+json")
        r.ContentType <- if h then "application/problem+json" else "application/json"
        r

[<ApiController>]
[<Route("[controller]")>]
type StoriesController() as this =
    inherit ControllerBase()

    let env = new AppEnv("URI=file:/home/rh/Downloads/scrumfs.sqlite") :> IAppEnv
    let acceptHeaders = this.Request.Headers.Accept

    [<HttpGet>]
    //[<Route("test")>]
    member _.GetById() : string = "Hello from F# and ASP.NET Core!"

    [<HttpPost>]
    member _.Create(ct: CancellationToken) : Task<ActionResult> =
        task {
            try
                let! result =
                    StoryAggregateRequest.CreateStoryCommand.runAsync
                        env.StoryRepository
                        env.SystemClock
                        env.Logger
                        ct
                        { Id = Guid.NewGuid(); Title = "Abc"; Description = Some "Def" }
                do! env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                    | Error e ->
                        match e with
                        | ValidationErrors es ->
                            es
                            |> Rfc7807Error.fromValidationError
                            |> Rfc7807Error.toJsonResult acceptHeaders
                            :> ActionResult
                        | DuplicateStory e -> raise (UnreachableException(string e))
            with e ->
                do! env.RollbackAsync(ct)
                env.Logger.LogException(e)
                return Rfc7807Error.createInternalServerError |> Rfc7807Error.toJsonResult acceptHeaders :> ActionResult
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
