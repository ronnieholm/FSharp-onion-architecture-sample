# TODO

- Apply code changes when .NET 8 releases on Nov 14, 2023: https://www.reddit.com/r/fsharp/comments/16ji5k2/comment/k0t2hji/.
- Why does ASP.NET errors not conform the the JSON config? Invalid GUID with model binding, for instance.
- Experiment with storing aggregates as documents within SQLite.
- Create pagable get-all-tasks, get-domain-event.
- Return error codes (cases of error DUs) in JSON error response, inspired by https://www.youtube.com/watch?v=AeZC1z8D5xI for Dapr.
- Make logs appear in the console.
- Add k6 load script
- Create F# script/console app to drive the application
- Create Bash script to call every endpoint (https://github.com/minio/mc/blob/master/functional-tests.sh)
- Split runDecorator into a pipeline of functions (https://github.com/ronnieholm/Playground/tree/master/CSDecoratorPattern)
- Add github build action (https://github.com/Zaid-Ajaj/pulumi-converter-bicep/blob/master/.github/workflows/test.yml)
- Add estimate fields
- Consider removing select type annotations where type is obvious from context
- Log user object together with request
- Include fscheck tests (https://github.com/jet/equinox/blob/master/tests/Equinox.CosmosStore.Integration/AccessStrategies.fs)
- SQL exist vs count: https://blog.devops.dev/the-difference-between-exists-and-count-0-in-sql-7f941c43255f