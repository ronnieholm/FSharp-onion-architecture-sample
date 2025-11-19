#r "nuget: FSharp.SystemTextJson, 1.3.13"

// $ dotnet fsi docs/json-serialization.fsx

open System.Text.Json
open System.Text.Json.Serialization

// 1. Either create the serializer options from the F# options...
let options =
    JsonFSharpOptions.Default()
        // Lower-case 'case' and 'field'.
        // Lower-case other parts of JSON with a policy
        // Compared to System.Text.Json serialization (is the F# library needed?)

        // Add any .WithXXX() calls here to customize the format
        .WithUnionNamedFields(true)
        //.WithUnionFieldNamesFromTypes(true)
        //.WithUnionUnwrapRecordCases(true)
        .ToJsonSerializerOptions()

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
printfn "%s" (JsonSerializer.Serialize(Some "foo", options))
printfn "%s" (JsonSerializer.Serialize(None, options))

type someUnion =
    | X of first: string * second: int
    | Y of (string * int) option
    | Person of Person * Other: string
printfn "%s" (JsonSerializer.Serialize(X("foo", 42), options))
printfn "%s" (JsonSerializer.Serialize(Y(None), options))
printfn "%s" (JsonSerializer.Serialize(Person ({ Firstname = "John"; Lastname = None }, "abc"), options))

