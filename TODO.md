# TODO

- Doc: Make note of how CLI tools can be fsx scripts, skipping console argument parsing
- Create console app to initialize database, including migrations (in web app with cmdline arg?)
- Link to C# and Go versions and red and blue books.
- Explore UMX library to add units of measure to every type to avoid memory overhead of wrapping basic types.
- Use System.Text.Json: System.Json.Text custom converter
  - https://twitter.com/ursenzler/status/1618956836245479425
- Re-implement Respawn features or use Docker to run each test in a container (see Nick's video)
  - Docker containers seem harder to manage in a CI/CD pipeline
- Doc: Don't assume high RPS as SQLite writing is by-design is sequential
- Move open inside relevant modules
- Add dev container file