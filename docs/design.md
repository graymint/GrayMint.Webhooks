# Design

What the code holds, what the database holds, and why each is where it is.

## The parts

- **`Messages`**: what a host does with its webhooks. `WebhookMessageService` queues a message and reads how messages and their attempts stand; `WebhookMessage` and `WebhookAttempt` are what it hands back.
- **`Delivery`**: what sends them. `WebhookDeliveryService` is a run: it finds the due messages and gives each free slot the next one. `WebhookTurn` is one message's turn: it claims the message, asks the host for its subscriber, has it sent, and writes what came of it. `WebhookSender` makes the one request; `WebhookAddressPolicy` says where a request may go; `WebhookSignature` signs it; `WebhookDeliveryJob` runs the delivery in a host. `IWebhookSubscribers` is the host's.
- **`Persistence`**: the module's own context and rows, internal throughout, with the setup that makes the tables and the session every call holds the database through.

## Why inside the app

A webhook tells of a change the app made, so it has to be queued by the transaction that made the change: a message sent for a change that rolled back, or a change no message tells of, is the fault a queue is there to prevent. That takes a table in the app's own database whatever sends the messages afterwards. With the table there, sending them is one request and a schedule, and a service of its own, with its broker, adds a deployment for it.

What such a service would take away is the sending itself: receivers that are slow, and the reach of a subscriber's address, would be on other machines than the app's. A host gets much of that by running the delivery in hosts of its own, which reach least of its network. An app that outgrows this hands its messages to such a service; the table stays.

## What the database holds

Three tables in the schema `gmwebhook`, made by the setup from the model, with no migrations: `Metadata.SchemaVersion` says which shape they have, and a start refuses another.

- **`Messages`**: one row for each webhook. `MessageId` is the order the messages were queued in, and never leaves the module. `Uid` is what the message's id is made of, for the host and the receiver: a UUID, so the id tells nothing of how many messages there are and is unique beyond this database. `Status` is `Pending`, `Succeeded` or `Failed`; `NextAttemptTime` is when a pending one is due; `LifetimeMilliseconds` is how long it was queued to live, from which `ExpirationTime` is counted, again when it is put back by hand; `LockToken` and `LockedUntil` are the hold of the delivery at work on it.
- **`Attempts`**: one row for each request made, written with what it did to its message, and never changed.
- **`Metadata`**: one row.

A subscriber's id and a subject compare by every character, their case too, on both databases: one subscriber's messages are never another's whose id differs by its case. Neither ends in white space: SQL Server compares a text as if the spaces at its end were not there, so one that ends so is refused as a message is queued, and names nothing where a message is looked for. No text holds a NUL character, which PostgreSQL refuses and SQL Server keeps.

A host that needs a join with its own tables, as for a report, maps the tables by the names of `WebhooksSchema`. It reads them, and removes the messages that are done and old enough, whose attempts go with them: the module removes none by itself. It changes nothing else.

## The host's transaction

On the host's connection (`AddGrayMintWebhooksDb<THostDbContext>`) every call works in the transaction the host's context has open. A call that writes takes a savepoint first, so what it wrote is taken back when it fails and the host's transaction stays usable; with no transaction open it runs in one of its own. A call that only reads takes none: when it fails inside the host's transaction, as by a cancel, that transaction is the host's to roll back.

Nothing a call read or wrote stays in the module's context for the next one. The context does keep hold of a host's transaction it joined, until a call finds that the host has another one, or none.

## A run of the delivery

A run keeps up to `MaxConcurrentDeliveries` messages in their turns at once. A slot that is free takes the next message at once, whatever the others are at, so a receiver that is slow keeps one slot busy and no other message waiting.

1. **Find.** The pending messages that are due and held by no delivery, leaving out any whose subscriber and subject have an older pending message: first attempts before retries, then the longest due first. What a look found is handed out for a second; then the run looks again, so a message queued meanwhile is not kept behind a long list of retries.
2. **Claim.** One statement takes the message for this delivery, and checks again that it may go: of two hosts that found it due, one changes the row.
3. **Ask the host** for the subscriber. No subscriber gives the message up. A host that can't say, or has not said after 10 seconds, keeps the message waiting a minute, and counts no attempt; what went wrong on the host's side goes to the log, and the message says only that it waits.
4. **Send**: a POST of the payload, with the subscriber's headers and the signature, within the attempt's timeout. Only the status counts, and the receiver's body is never read.
5. **Write** the message, in a statement that checks this delivery still holds it, and then its attempt, in one transaction, within 20 seconds even while the host stops. A delivery whose hold ran out, or whose message was removed meanwhile, changes no row and writes no attempt.

The hold of a claim lasts as long as the steps after it may take together: 10 seconds to get to the request, the attempt's timeout, and 20 seconds to write. A turn that took longer than its 10 seconds to get to the request, by a claim that waited on the database or a lookup that ran on, makes no request: one made so late could still be under way when another delivery takes the message.

The run looks again while there may be something to find: after a look that took a whole batch, and after a turn that ended, which lets the next message of its subject go. A run that fails, as on a database that is gone for a moment, cuts off no turn under way: each ends in its own time and writes what it has sent.

Every time is the host's own clock's. Hosts keep their clocks together: a host whose clock runs more than 20 seconds ahead of another's could take a message the other is still sending, and the receiver would get it twice.

The job runs it every `PollInterval`, for the retries and for what another host queued, and sooner when a message is queued in its own host: as the message is committed, when the call that queued it committed it; and as the scope that queued it ends, when it is in a transaction of the host's, since the module can't see that transaction commit. A scope that lives on long after its transaction, or ends before it, leaves the message to the next look. A run that fails is logged, and the next one runs.

## How an attempt ends

| What happened | The message |
| --- | --- |
| `2xx` | Succeeded. |
| `410` | Failed: the receiver takes it no more. |
| Any other answer, no answer in time, no connection, a name with no public address, a header that can't be sent, a secret that is not base64, or no secret | Pending, due after the wait of this attempt's number; failed after the last one, or when that wait would pass its expiry. |
| Its expiry passed before an attempt | Failed, with no request. |
| An address that is not https, or is a number that is not public | Failed, with no request: no later attempt can find it otherwise. |
| The host could not say whose it is, or its turn got to the request too late | Pending, due in a minute, with no attempt counted. |
| The host is stopping | As it was, held no more, with no attempt counted. One claimed in the moment the host stops stays held until its hold runs out. |

Delivery is at least once: a message whose answer is lost, or whose host dies between the request and the write, goes again.

## The order of a subject

A later message of a subscriber and subject is not due while an older one is pending, so they go one at a time, by `MessageId`. That is the order the messages were queued in, and it is the order of the host's changes when its transactions queue for one subject one after another: a message whose transaction commits later than a newer one's can't be seen until then, and the newer one goes first. A host sees to it by changing the subject's own row, such as the order's, before it queues, in the same transaction, which makes a second transaction wait for the first. A message put back by hand goes after those of its subject that were delivered meanwhile.

On SQL Server without `READ_COMMITTED_SNAPSHOT`, a look waits for a transaction that has queued a message, where PostgreSQL does not see the message until it commits: every host's delivery waits as long as that transaction is open. The database's option takes that wait away.

## Where a message may go

`WebhookAddressPolicy` says which addresses are public. In IPv4 they are all but IANA's special ones: this machine, private and shared networks, link-local, documentation, multicast and reserved ranges. In IPv6 they are the global unicast space less its special ranges, so a range that is named later is closed without a change; and an IPv4 address inside an IPv6 one that leads to it is judged as that address. A public address of the sending machine's own network interfaces is closed too: it is the machine itself, reached past its firewall with its own address as the caller's.

`WebhookSender` owns its HTTP client, so no setting of a host lifts the guard: it resolves a name itself as it connects and goes only to such an address of it, follows no redirect, uses no cookie or proxy, and speaks HTTP/1.1, as HTTP/3 connects another way. `AllowInsecureTargets` opens all of it, for development and tests.

The module can't know the public addresses of the host's other servers, or a public address that something else maps to the sending machine. A firewall on the way out of the hosts that deliver closes those.

## Choices

- **The module's own context, not the host's model.** As the other GrayMint modules: its tables are its own to shape, and a host's migrations never hold them. The host's transaction is joined instead.
- **The host says who a subscriber is.** Secrets and headers stay where the host keeps them, are read before every attempt, and are never copied into the queue.
- **A message never goes unsigned.** A subscriber with no secret gets a failed attempt, not a request that nobody can verify.
- **The address is each message's own.** A host that keeps one address for a subscriber passes it as it queues.
- **A schedule that ends.** A message is given up after its last attempt, and put back by hand: one that is retried for ever hides a receiver that is gone.
- **A host's bad setting is an attempt.** A header or a secret that can't be sent fails the attempt, so the message ends with its schedule; counted as a wait, it would go on every minute for ever.
- **One host makes the tables.** The setup runs any number of times, one at a time; a second host that runs it at the same moment fails at its start, and starts at its next try.
- **Not included**: the removal of old messages, which the host does; an endpoint's suspension after repeated failures; a receiver's verification, which the specification's libraries do.
