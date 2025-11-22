#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: FSharp.UMX, 1.1.0"

// $ dotnet fsi docs/json-serialization.fsx

open System
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.UMX

// 1. Either create the serializer options from the F# options...
let options =
    JsonFSharpOptions.Default()
            // Add any .WithXXX() calls here to customize the format
        .WithUnionNamedFields(true)
        //.WithUnionFieldNamesFromTypes(true)
        //.WithUnionUnwrapRecordCases(true)
        .ToJsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

// 2. ... Or add the F# options to existing serializer options.
JsonFSharpOptions.Default()
    // Add any .WithXXX() calls here to customize the format
    .AddToJsonSerializerOptions(options)

// 3. Either way, pass the options to Serialize/Deserialize.
let s = JsonSerializer.Serialize({| x = "Hello"; y = "world!" |}, options)
printfn $"{s}"
// --> {"x":"Hello","y":"world!"}

type Person =
    { Firstname: string
      Lastname: string option }

type someOption = string option
printfn "%s" (JsonSerializer.Serialize(Some "foo", options)) // --> "Foo"
printfn "%s" (JsonSerializer.Serialize(None, options)) // --> null

type SomeUnion =
    | X of first: string * second: int
    | Y of (string * int) option
    | Person of Person * Other: string
printfn "%s" (JsonSerializer.Serialize(X("foo", 42), options)) // --> {"Case":"X","Fields":{"first":"foo","second":42}}
printfn "%s" (JsonSerializer.Serialize(Y(None), options)) // --> {"Case":"Y","Fields":{"Item":null}}
printfn "%s" (JsonSerializer.Serialize(Person ({ Firstname = "John"; Lastname = None }, "abc"), options)) // --> {"Case":"Person","Fields":{"Item1":{"Firstname":"John","Lastname":null},"Other":"abc"}}

// 4. Does unit of measure serialize to plain forms? Yes.
[<Measure>] type customerId
[<Measure>] type orderId
[<Measure>] type kg

type Order =
    { Id: Guid<orderId>
      Customer: string<customerId>
      Quantity: int<kg>
      SomeUnion: SomeUnion }

let order =
    { Id = % Guid.NewGuid()
      Customer = % "customerId"
      Quantity = % 42
      SomeUnion = X("foo", 42) }

printfn "%s" (JsonSerializer.Serialize(order, options))
JsonSerializer.Deserialize<Order>(@"{""id"":""e6ea8113-ea51-4de8-834c-a8107e444bcd"",""customer"":""customerId"",""quantity"":69,""someUnion"":{""Case"":""X"",""Fields"":{""first"":""foo"",""second"":42}}}", options)
