# TODO

- Add estimate field
- Why does ASP.NET errors not conform the the JSON config? Invalid GUID with model binding, for instance.
- Return error codes (cases of error DUs) in JSON error response, inspired by https://www.youtube.com/watch?v=AeZC1z8D5xI for Dapr.
- Create F# script to call every endpoint (https://github.com/minio/mc/blob/master/functional-tests.sh)
- Add github build action (https://github.com/Zaid-Ajaj/pulumi-converter-bicep/blob/master/.github/workflows/test.yml)
- Does SQLite handle transactions updating the same row or is it last write wins?
- Use given (class), then (method) pattern in tests?
- Create assert helpers for comparing composite types?
- Add OpenTelemetry
  - https://www.youtube.com/watch?v=tctadmNTHfU
  - https://www.youtube.com/watch?v=nFU-hcHyl2s
- Add RowVersion to each aggregate/entity per https://www.youtube.com/watch?v=YfIM-gfJe4c (we can used modified timestamp as rowversion column, but better add a rowversion specific column).
- Switch from HTTP 400 to HTTP 422 (https://youtu.be/x7v6SNIgJpE?t=4245)
- Create sharedErrors DU to reduce boilerplate code in Giraffe handlers. 
- Include fscheck tests (https://github.com/jet/equinox/blob/master/tests/Equinox.CosmosStore.Integration/AccessStrategies.fs)
- Write stateful property based test, generating events to the data access
  layer. Test maintains in memory aggregate by processing each event, updating
  the aggregate, and then compares aggregate with what's returned by the
  database (https://youtu.be/LvFs33-1Tbo?t=1786). Possibly the event handler can
  be reused from the aggregate if instead of making the update inside domain
  functions make the update through it calling a handler event function. See also https://aaronstannard.com/fscheck-property-testing-csharp-part3/
