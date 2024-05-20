# ADR0004: When to persist store changes

Status: Accepted and active.

## Context

Saving changes to the store may happen in one of multiple places, each with pros
and cons.

Ideally, we want every change made by one handler and subscribed event
processors to go into a single transaction.

### Inside application layer handler

One place to save changes is towards the end of each command handler. The
handler may save changes and possibly read back an aggregate to return (although
unless the database calculates field value it's redundant work). Relying on save
before get means the application layer isn't persistence ignorant, e.g.,
repository code setting `createdAt` or updating `updatedAt` (on domain types) to
avoid repeating the logic across the domain.

A downside is that one can forget to save or save multiple times.

It's tempting to couple independent systems. Imagine a handler which saves a new
entity, then sends an email. Only with a distributed transaction encompassing
both database and email system are both guaranteed to either succeed or fail: if
saving to store is executed first, it's guaranteed to succeed or fail before
attemtping to send the email sending. But if sending email fails, we already
updated the database.

We could take the saga route so that if email sending fails, a compensating
transaction is issued against the database. But by then another system might
have picked up the changes and updated additional state.

### Outside Applications layer handler

With this approach, handler logic remains the same, except save happens at the
level above the handler. In a ASP.NET application, save from the controller
action would be one option. With core hosted by ASP.NET, one HTTP request
typically maps to one handler. Other hosts, such as a console or service may
batch multiple updates. The higher level save approach supports both.

### Comparison with typical C# + EF approach

With C# + EF, it's common to call `DbContext.Save` in each handler. The `Save`
method is typically called on a `DbContext` derived class, talking advantage of
EF change tracking to identify created or updated aggregates.

Within each aggreate, the root stores the list of domain events to be published
by the `Save` method. As publishing an event can generate more events,
publishing goes on until no more events are present in any changed aggregate.

With this approach, `Save` has multiple responsibilities (such as publishing
events to processors), but it fits well into EF's way of working. In our
application, we have no conceptual equivalent of `DbContext`, and prefer more
expclit publishing of events within each handler.

## Decision

Chaining multiple systems, such as the database and an email system, may be
better done by a separate job picking up the newly created entity. The job then
attempts to send the email multiple times until it succeeds or gives up.

Such job could track new entities by recording its last succesfully processed
`createdAt` timestamp, through a separate field on the entity, or through the
handler posting a message to a queue. Not a queue in another systems as that's
the problem we're trying to prevent, but a queue table within the database.

Storing queues in a database may not work at FAANG scale, but is adequate for
many business applications. It simplifies application logic and reduces the
number of failure modes.

## Consequences

We save outside handlers for the flexibility and persistence ignorance.

## See also

- [Life beyond Distributed Transactions: an Apostate’s Opinion (paper, 2007 version) - Pat Helland](https://ics.uci.edu/~cs223/papers/cidr07p15.pdf).
- [Life beyond Distributed Transactions: an Apostate’s Opinion (paper, 2016 version) - Pat Helland](https://dl.acm.org/doi/pdf/10.1145/3012426.3025012).
- [Life Beyond Distributed Transactions: An Apostate's Implementation (talk) - Jimmy Bogard](https://www.youtube.com/watch?v=AUrKofVRHV4) with [code](https://github.com/jbogard/AdventureWorksCosmos).
- [Sean T. Allen on Life Beyond Distributed Transactions: An Apostate’s Opinion [PWL SF] 07/2018](https://www.youtube.com/watch?v=xI56ox7dcRQ).
