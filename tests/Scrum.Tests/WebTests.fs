namespace Scrum.Tests.WebTests

// We don't use the TestServer because it's a thin wrapper around traditional
// integration tests. It adds more complexity than it takes away:
//
// See https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests
//
// It's most useful with C# services.
//
// The F# solution only makes minimal use of the dependency injection
// container, so TestServer doesn't provide a way to mock most dependencies.

open Xunit

type WebTestsTests() =
    [<Fact>]
    let ``Todo`` () = ()
