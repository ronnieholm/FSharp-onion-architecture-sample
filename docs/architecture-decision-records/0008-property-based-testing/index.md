# ADR0008: Property based testing

Status: Accepted and active.

## Context

Instead, or as a supplement, to regular integration tests for each command or
query, write these tests as property based tests. Reserve manually written tests
for special cases, and have property based cover as much as possible.

At the individual command or query level, the property based tests has the
advantage that fixed A pattern test data generation is replaced by randomized
test data generated, which is more likely to reveal bugs than fixed test data.

When an integration test needs to combine more than one command or query,
consider making it part of a state based property test. For instance an update
task test relies on creating a story and then adding a task to the story, which
can then be updated. It makes for a boilerplate heavy typical integration test.

With state based property tests, multiple paths through commands can be randomly
exercised. For instance before updating the task (generally) the story can have
any series of intermediate commands applies after its creation, whose
permutation would be difficult to capture in a typical integration test.

Another issue is at which level to perform the test. At the lowest level, the
domain, property based tests may be carried out at the aggregate or value type
level. But tests which exercise more of the underlying system are generally
preferable.

A better place is thus at the application level, injecting commands or
queries. It exercises integration to application mappings and back, database SQL
statements, and database to domain mappings and back.

Finally, tests could be carried out from the integration level as HTTP. While it
exercises every more mapping code, it has the downside that dependencies inside
application layer cannot easily be controlled.

Suppose we called out to another system to send email. Then in tests at this
level, it would be significant work to mock such systems. Inside an application
level tests, it's a matter of providing a mock implementation and then query the
mock for which interaction it encountered during the test or insert fake
responses.

## Decision

Following John Hughes adage of "don't write tests. Generate them", use property
based tests where applicable. Regular tests are still needed, but not in the
same numbers. Regular tests would be useful to capture bugs or regressions.

## Consequences

With property based tests, less test code is needed, but path coverage is
larger.
