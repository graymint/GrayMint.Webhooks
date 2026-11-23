# GrayMint.Webhooks

The webhooks a host sends: queued in tables of their own on PostgreSQL or SQL Server, in the host's transaction when it
has one open, and delivered by any of its hosts with retries, signed as the Standard Webhooks specification says, to
public https addresses only.
Read [docs/design.md](docs/design.md) before changing code: it says what the code holds, what the database holds,
and why each is where it is. Keep it, and the README, true in the same change that makes them wrong.

## Rules

1. **The repository is general.** Its code, docs, tests and commit messages name no other product, company or
   person. Examples use neutral names: a shop, an order, `example.com`.
2. **A guard comes with its test, written so it can't pass for another reason.** Whatever keeps the webhooks right
   (a claim, a hold's end, the time a turn has, a subject's order, the address policy, the connect's check, a
   redirect, the schedule, an expiry, a subscriber's wall, the setup's checks, what a request may be) gets a test
   that checks exactly what it refuses, and that nothing was kept or sent. A test that needs two things to meet
   makes them meet, by the receiver's hook or the command interceptor's, and never by a sleep that hopes.
3. **A message is one delivery's at a time.** A delivery claims it in one statement before it sends it, and writes
   its outcome in one statement that checks it still holds it. Nothing else changes a pending message, and no row
   is changed from a copy that was read before. A claim's hold is as long as the steps after it may take together:
   a step that gets more time gets it in the hold too.
4. **A message and its attempt are written together, the message first.** The queue and the history always agree,
   and a message that was removed meanwhile gets no attempt.
5. **The sender's client is its own.** No option, and no service a host registers, replaces its handler: the address
   is judged at the connect, a redirect is never followed, and no cookie or proxy is used.
6. **Tests run on a real PostgreSQL, a real SQL Server and a real receiver.** `docker compose up -d`, then
   `dotnet test`. Every case that touches the database runs on both (`[EveryDatabase]`), each in a database of its
   own, made as the setup makes it; a case of the delivery runs on each both ways a host has the module, on a
   connection of its own and on the host's (`[EveryDatabaseAndConnection]`). The receiver is a server on a port of
   the machine. Run them after every change; a change isn't done until they pass.
7. **One model for both databases.** Whatever EF can say goes through EF. The package names no database provider:
   the host names its own.
8. **Fail loudly.** A setting that is wrong stops the registration; no database named for the module, a database
   without the tables, or with tables of another version, stops the start; so does a host that delivers without its
   subscribers. The delivery's job logs a run that fails and runs the next: a stopped delivery keeps every webhook
   waiting without a word. What fails on the host's side goes to the log, and never into a message, which a host may
   show its subscriber.
9. **.NET 11**, named by `global.json`.
10. **A change to the tables raises their version.** The start takes tables of `WebhooksSchema.Version` alone. A
    change to the model raises it, and gives the setup a step that takes tables of the version before to it, with a
    test that runs the step on such tables, in the same change.

## Code

- One type per file, whatever its visibility. `Persistence` is internal throughout: a host sees the services, the
  options, the classes they take and give, `IWebhookSubscribers`, `WebhooksSchema`, `WebhookLimits` and the
  registration, and nothing else. A service's constructor is internal, so only the registration makes one.
- Every public type and member has an XML doc (`///`): what a caller gets, and what refuses it. The build fails
  without one.
- A message's id goes out as text, made of a UUID; a row's own key never goes out. Every time is UTC, written as
  `TimeProvider` gives it and read back as UTC; a test moves the clock.
- Fields and state at the top of a class. No `!` to silence a null: check it, or throw with what is wrong.
- A method that waits is async and takes a required `CancellationToken`, which it passes on. A method hands back one
  named type, never a tuple; a list is an `IReadOnlyList<T>` or `IReadOnlyCollection<T>`.
- A comment says why the code is there, or which tempting change would break what. No history, and nothing the code
  already says.
- No top-level statements. Nothing temporary in the repository: scratch goes to the system's temp folder.

## Publishing

Every push to `main` publishes the `GrayMint.Webhooks` package to nuget.org, so `main` only gets what is ready to
ship. CI builds, runs the tests on the services of `compose.yaml`, raises the version in `pub/PubVersion.json` and
`src/Directory.Build.props` with a commit of its own, and pushes the package; a failed test stops it before the
version moves. Never change the version by hand. `publish_nugets.yml` is a thin wrapper around the shared workflow of
`graymint/GrayMint.Common`, and nuget.org takes the package only from that file in this repository (a trusted
publishing policy, made on nuget.org before the first publish), so renaming the file or the repository stops
publishing.

The README is the package's page on nuget.org.
