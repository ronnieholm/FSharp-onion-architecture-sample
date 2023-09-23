﻿namespace Scrum.Web

open System
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Mvc
open Scrum.Application
open Scrum.Application.Seedwork
open Scrum.Infrastructure

[<ApiController>]
[<Route("[controller]")>]
type StoryController() =
    inherit ControllerBase()

    let env = AppEnv("URI=file:/home/rh/Downloads/scrumfs.sqlite") :> IAppEnv

    [<HttpGet>]
    //[<Route("test")>]
    member _.GetById() : string = "Hello from F# and ASP.NET Core!"

    [<HttpPost>]
    member _.Create(ct: CancellationToken) : string =
        // TODO: https://softwareengineering.stackexchange.com/questions/314066/restful-api-should-i-be-returning-the-object-that-was-created-updated
        let id =
            StoryAggregateRequest.CreateStoryCommand.runAsync
                env.StoryRepository
                env.SystemClock
                ct
                { Id = Guid.NewGuid(); Title = "Abc"; Description = Some "Def" }

        match id.Result with
        | Ok id -> "id"
        | Error e -> "error"

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
