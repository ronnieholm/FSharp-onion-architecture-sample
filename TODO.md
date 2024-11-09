# TODO

- Add enumeration field
- Add estimate field
- Why does ASP.NET errors not conform the the JSON config? Invalid GUID with model binding, for instance.
- Return error codes (cases of error DUs) in JSON error response, inspired by https://www.youtube.com/watch?v=AeZC1z8D5xI for Dapr.
- Create F# script/tests to call every endpoint (https://github.com/minio/mc/blob/master/functional-tests.sh) using fshttp.
- Add github build action (https://github.com/Zaid-Ajaj/pulumi-converter-bicep/blob/master/.github/workflows/test.yml)
- Use given (class), then (method) pattern in tests?
- Create assert helpers for comparing composite types?
- Add OpenTelemetry
  - https://www.youtube.com/watch?v=tctadmNTHfU
  - https://www.youtube.com/watch?v=nFU-hcHyl2s
  - https://www.youtube.com/watch?v=MHJ0BHfWhRw
- Add RowVersion to each aggregate/entity per https://www.youtube.com/watch?v=YfIM-gfJe4c (we can use modified timestamp as rowversion column, but perhaps better add a rowversion specific column. Redudant for SQLite which only supports one transaction at a time, but good for read committed general solution. Not needed for actor based solution).
- Switch from HTTP 400 to HTTP 422 (https://youtu.be/x7v6SNIgJpE?t=4245)
- Include fscheck tests (https://github.com/jet/equinox/blob/master/tests/Equinox.CosmosStore.Integration/AccessStrategies.fs)
- Write stateful property based test, generating commands/queries. Test can maintain in memory aggregate state, then compares aggregate with what's returned by the
  database (https://youtu.be/LvFs33-1Tbo?t=1786). Possibly the event handler can
  be reused from the aggregate if instead of making the update inside domain
  functions make the update through it calling a handler event function. See also https://aaronstannard.com/fscheck-property-testing-csharp-part3/
- https://www.compositional-it.com/news-blog/working-with-phantom-types-in-fsharp/
- Add email sending service, storing emails in database for separate processing
- Perform experiment copying Story aggregate out into seperate projects for main code and test code (vertical slice architecture)
- Use vscode user as with https://github.com/dotnet/orleans/blob/main/.devcontainer/devcontainer.json.
- Create Orleans and Akka.NET branches.
- Current database write queries are only valid with isolation level serialization (https://rfd.shared.oxide.computer/rfd/0192)
- Simulation testing: https://www.youtube.com/watch?v=N5HyVUPuU0E and https://www.youtube.com/watch?v=N5HyVUPuU0E and https://www.youtube.com/watch?v=UZkDdQEoolo
- Investing transactions in middleware: https://blog.bencope.land/f-crud-api-with-giraffe-mysql-and-dapper-fsharp. Intestingly, this is such example: https://www.youtube.com/watch?v=EUdhyAdYfpA
- How to measure time the optimized way: https://www.youtube.com/watch?v=Lvdyi5DWNm4
