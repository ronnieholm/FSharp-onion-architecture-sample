# TODO

- Include fscheck tests (https://github.com/jet/equinox/blob/master/tests/Equinox.CosmosStore.Integration/AccessStrategies.fs)
- Add estimate field
- Split runDecorator into a pipeline of functions (https://github.com/ronnieholm/Playground/tree/master/CSDecoratorPattern)
- Consider removing select type annotations where type is obvious from context
- Why does ASP.NET errors not conform the the JSON config? Invalid GUID with model binding, for instance.
- Return error codes (cases of error DUs) in JSON error response, inspired by https://www.youtube.com/watch?v=AeZC1z8D5xI for Dapr.
- Create F# script/console app to drive the application
- Create Bash script to call every endpoint (https://github.com/minio/mc/blob/master/functional-tests.sh)
- Add github build action (https://github.com/Zaid-Ajaj/pulumi-converter-bicep/blob/master/.github/workflows/test.yml)
- Apply code changes when .NET 9 releases on Nov 14, 2023: https://www.reddit.com/r/fsharp/comments/16ji5k2/comment/k0t2hji/ and https://www.youtube.com/watch?v=9172tKgSaKc.
- Does SQLite handle transactions updating the same row or is it last write wins?
- Use given (class), then (method) pattern in tests?
- Create assert helpers for comparing composite types?
- Add OpenTelemetry (https://www.youtube.com/watch?v=tctadmNTHfU)
- Experiment with https://devblogs.microsoft.com/dotnet/a-new-fsharp-compiler-feature-graphbased-typechecking/
- Use in SQLiteRepository for joins: https://learn.microsoft.com/en-us/dotnet/api/system.collections.objectmodel.keyedcollection-2?view=net-7.0&redirectedfrom=MSDN