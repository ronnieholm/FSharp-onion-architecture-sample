namespace Scrum.Tests.WebTests

// We don't use the TestServer because it's a thin wrapper around traditional
// integration tests. It adds more complexity than it takes away:
//
// See https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests
//
// It's most useful with simple, single layer C# services.
//
// Our solution only makes minimal use of the dependency injection container,
// so TestServer doesn't provide a way to mock most dependencies. Only for
// solutions with simple dependencies, such as a database, is the TestServer
// useful.
//
// Another downside is that for payloads the tests operate at the  textual
// level, whereas traditional integration tests stay in object world. The tests
// are therefore most useful for verifying aspects of HTTP, such as HTTP status
// code, not payload.

open Xunit

type WebTestsTests() =
    [<Fact>]
    let ``Todo`` () =
        ()
