# ADR013: When to save changes to the store

## Context

Saving changes to the store can happen in one of multiple places, each with
their pros and cons.

### Inside Application layer handler

One place to save changes is towards the ends of each command handler (the
`runAsync` function). This way the handler can save changes and read back a
aggregate to return. Relying on save before get, though, is indicative of the
application layer not being persistence ignorant, e.g., repository code setting
`createdAt` or updating `updatedAt` to avoid repeating the logic across the
domain. Obviously, save would need to happen somewhere after the handler
completes and before determining the HTTP status code and response.

Another downside is that one can forget to save or worse save multiple times
(perhaps because the application layer isn't persistent ignorant). Ideally, we
want every change made by one handler to go into one transaction. This includes
changes made by handlers subscribed to an event.

Also, it's tempting to couple indepedent systems. Imagine a handler which saves
a new entity, then sends out a email. Only with a distributed transaction
encompassing both database and email systems can we guarantee both succeed or
both rolls back. As save to store comes first, it's guaranteed to succeed, while
email sending can fail. We could go with a saga approach so that when email
sending fails, a compensating transaction removes entity from the database. But
that's a complex approach, permiating through dependent systems, e.g., a third
system who picked up the entity needs to issue a compensating transaction as
well.

### Outside Applications layer handler

With this approach, handler logic remains the same, but save takes place at a
level above the handler. In a ASP.NET application, save from the controller
action is one option. With core hosted by an ASP.NET application, one HTTP
request typically maps to calling one handler. Other hosts, such as a console or
service may batch multiple updates. The higher level save approach becomes
flexible.

### Comparison with typical C# + EF approach

With C# + EF, it's common to call `DbContext.Save` in each handler. The `Save`
method is typically called on `DbContext` derived class, using change tracking
to iterate newly created or updated aggregates. Without each aggreate, the list
of events stored by the root is published. As publishing an event can generate
more events, publishing goes on until no more events are present. With this
approach, `Save` has multiple responsibilities, fitting well into EF's way of
working. In out application, we have no conceptual equivalent of `DbContext`,
and we prefer the more expclit publishing of events within each handler (see
ADR009 for details).

## Decision

Chaining multiple systems is more robustly done by a separate job picking up the
newly created entity, then attempting to send the email multiple times until it
succeeds or permanently fails. Such job could keep track of new entities by
recording its last succesfully processed `createdAt` or `updatedAt` timestamp,
through a separate field on the entity, or through the handler posting a message
to a queue. Not a queue in another systems as that's the problem we're trying to
prevent, but a queue table within the same database.

Storing queues in a database may not work at FANG scale, but is perfectly
adequate for many business applications, simplifying the application and
reducing the number of failure modes.

Until disproven by a business requirement, we save outside handlers. If for some
reason, we need to save inside a handler, we could pass in the `AppEnv`'s commit
function as a dependency similar to other dependencies.

If required, we could detect multiple calls to save within the scope of an
`AppEnv` instance, or unsaved data within a transaction which should've been
explicitly rolled back.

Currently, the `IDisposable` implementation on `AppEnv` rolls back any
uncommited changes before releasing memory for the unmanaged transaction and
connection objects.

## Consequences

Keeping save outside each handler makes for a more robust and flexible approach.

## See also

- [Life beyond Distributed Transactions: an Apostate’s Opinion (paper, 2007 version) - Pat Helland](https://ics.uci.edu/~cs223/papers/cidr07p15.pdf).
- [Life beyond Distributed Transactions: an Apostate’s Opinion (paper, 2016 version) - Pat Helland](https://dl.acm.org/doi/pdf/10.1145/3012426.3025012).
- [Life Beyond Distributed Transactions: An Apostate's Implementation (talk) - Jimmy Bogard](https://www.youtube.com/watch?v=AUrKofVRHV4) with [code](https://github.com/jbogard/AdventureWorksCosmos).
- [Sean T. Allen on Life Beyond Distributed Transactions: An Apostate’s Opinion [PWL SF] 07/2018](https://www.youtube.com/watch?v=xI56ox7dcRQ).
