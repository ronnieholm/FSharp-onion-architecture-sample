namespace Scrum.Tests.WebTests

// See https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.AspNetCore.TestHost
open Xunit
open Scrum.Web

type WebTestsTests() =
    [<Fact>]
    let ``xxx`` () =
        let builder = new WebHostBuilder()
        ()
