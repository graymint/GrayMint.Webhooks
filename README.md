# GrayMint.Webhooks

**The webhooks your app sends**: queued in tables of their own on PostgreSQL or SQL Server, in your own transaction when you have one; delivered by any of your hosts, with retries; signed as the [Standard Webhooks](https://www.standardwebhooks.com) specification says; and sent to public https addresses only.

[![NuGet](https://img.shields.io/nuget/v/GrayMint.Webhooks.svg)](https://www.nuget.org/packages/GrayMint.Webhooks)
[![License](https://img.shields.io/github/license/graymint/GrayMint.Webhooks.svg)](LICENSE)

An order is paid, and the shop's own server should hear of it. Your app queues a message in the transaction that marks the order paid; the module sends it, tries again when the shop's server is down, and keeps what happened. It runs inside your app: no other service, no broker.

```sh
dotnet add package GrayMint.Webhooks
```

## What it does

- **Queues with your change.** On your own connection, a message is written in the transaction you have open: it commits with the change it tells of, or goes with it. Nothing is sent for a change that was rolled back, and no change is left untold.
- **Delivers at once, and again.** A host that delivers makes the first attempt as the message is committed; for a message in a transaction of yours, as the scope that queued it ends. A message queued in a host that doesn't deliver goes at the next look of one that does, 10 seconds at most unless you say otherwise. A failed one is tried again after 5 seconds, 5 minutes, 30 minutes, 2, 5, 10 and 10 hours, the schedule the specification suggests: eight attempts over some 28 hours, and then the message is given up. You put a failed one back with `Retry`.
- **Keeps new messages moving.** A first attempt goes before any retry, and a receiver that is slow to answer keeps only its own slot busy: one subscriber whose server is down doesn't hold up the others' new messages.
- **Signs.** Every request has `webhook-id`, `webhook-timestamp` and `webhook-signature`: an HMAC-SHA256 of the id, the time and the body, as Standard Webhooks has it, so a receiver verifies it with one of that specification's libraries. Two secrets give two signatures, so a secret changes without a missed message. A message never goes unsigned.
- **Keeps an order.** A subscriber's messages of one subject, such as an order's id, are delivered in the order they were queued: a later one waits until the earlier one is delivered or given up. See [The order of a subject](#the-order-of-a-subject) for what that asks of you.
- **Runs in any number of hosts.** A delivery claims a message in the database before it sends it, and holds it for as long as it may take with it, so two hosts don't send the same one, and a host that dies with a message leaves it to the others. Delivery is at least once: a receiver may still get a message twice, as when its answer is lost on the way, and tells by `webhook-id`.
- **Guards your network.** A message goes by https to a public address alone, and never to an address of the machine that sends it. The address is judged as the socket connects to it, so a name can't resolve to one address for a check and to another for the connect; a redirect is an answer, and is never followed. See [What the guard covers](#what-the-guard-covers).
- **Keeps the history.** Each message has its status, and each attempt its time, its duration, the receiver's status code and what went wrong.

## Use

Your app gives the module one thing: what it knows of a subscriber as one of its messages goes.

```csharp
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;

// asked before every attempt, so a changed secret counts from the next one
public class ShopSubscribers(ShopDbContext dbContext) : IWebhookSubscribers
{
    public async Task<WebhookSubscriber?> Find(WebhookMessage message, CancellationToken cancellationToken)
    {
        var shop = await dbContext.Shops.FindAsync([message.SubscriberId], cancellationToken);
        return shop == null
            ? null // the shop is no more: its message is given up
            : new WebhookSubscriber { SigningSecrets = [shop.WebhookSecret] };
    }
}
```

Every host registers the module, on the connection of your own context:

```csharp
builder.Services.AddGrayMintWebhooks();
builder.Services.AddGrayMintWebhooksDb<ShopDbContext>((connection, options) => options.UseNpgsql(connection));
```

and each host that delivers, at least one, registers the delivery with its subscribers:

```csharp
builder.Services.AddScoped<IWebhookSubscribers, ShopSubscribers>();
builder.Services.AddGrayMintWebhookDelivery(options => options.UserAgent = "Shop-Webhooks/1.0");
```

The tables go in the database of that connection, in a schema of their own (`gmwebhook`), beside your tables. One host makes them before it starts, and every host checks them at its start:

```csharp
await app.Services.UseGrayMintWebhooks(cancellationToken);
```

Then a message is queued where the change is made:

```csharp
public class OrderService(ShopDbContext dbContext, WebhookMessageService webhooks)
{
    public async Task MarkPaid(Order order, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        order.State = OrderState.Paid;
        await dbContext.SaveChangesAsync(cancellationToken);

        await webhooks.Create(new WebhookMessageCreateRequest {
            SubscriberId = order.ShopId,
            Url = order.Shop.WebhookUrl,
            EventType = "order.paid",
            Payload = JsonSerializer.Serialize(new { type = "order.paid", data = new { orderId = order.Id } }),
            Subject = order.Id
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
```

`WebhookMessageService` also reads how messages stand (`Get`, `GetBySubscriber`, `GetAttempts`), puts a failed one back (`Retry`) and removes a subscriber's (`DeleteBySubscriber`). A message is its subscriber's alone: every call names the subscriber.

### A database of their own

`AddGrayMintWebhooksDb(options => options.UseNpgsql(connectionString))` puts the tables on a connection of their own, in a database of their own, which the setup makes, or in yours. A message is then queued at once, whatever transaction you have open.

### The order of a subject

A subject's messages go in the order they were queued, and that is the order of your changes when your transactions queue for one subject one after another. They do when each changes the subject's own row before it queues, as `MarkPaid` above changes the order: a second transaction then waits for the first to commit. Two transactions that queue for one subject at the same moment, with nothing between them, may be delivered in either order. A message put back with `Retry` goes after those of its subject that were delivered meanwhile.

### The secrets

A secret is base64, with `whsec_` before it or not, as the specification has it and as its libraries read it; it asks for 24 to 64 random bytes. A secret you keep as plain text goes in as the base64 of its bytes, and the receiver gives its library that same text. Every subscriber has at least one: a message with none is not sent, and its attempt fails. `WebhookSubscriber.Headers` adds headers of the subscriber's own to its messages, such as an `Authorization` it asked for.

### What a receiver does

It verifies `webhook-signature` with a Standard Webhooks library, refuses a `webhook-timestamp` that is too old, answers `2xx` within 15 seconds, and treats a `webhook-id` it has seen as done. An answer of `410 Gone` gives the message up at once.

### What the guard covers

Closed to a subscriber: the sending machine by any of its addresses, private and shared networks, link-local addresses, a cloud's metadata service among them, and every other address that is not a public one, in IPv4 and IPv6, also when it is wrapped in an IPv6 address that leads to it.

Not closed, as the module can't know them: the public addresses of your other servers, and a public address that something else maps to the sending machine. A firewall on the way out of the hosts that deliver closes those. Deliver from the hosts that reach least of your network. The sender goes to a receiver directly, through no proxy.

### Old messages

The module removes none. A message that is done, `Succeeded` or `Failed`, is yours to remove when it is old enough: map the tables by the names of `WebhooksSchema` and delete it, and its attempts go with it. A `Pending` one is the delivery's: leave it.

## Options

| `WebhookDeliveryOptions` | Default | |
| --- | --- | --- |
| `PollInterval` | 10 seconds | How often a host looks for due messages: the retries, and what another host queued. |
| `AttemptTimeout` | 15 seconds | For one attempt, from the address lookup to the receiver's status line. |
| `RetryDelays` | 5 s, 5 min, 30 min, 2 h, 5 h, 10 h, 10 h | The wait after each failed attempt. |
| `MaxConcurrentDeliveries` | 8 | The most attempts under way at once in a host. |
| `BatchSize` | 100 | The most due messages one look takes. |
| `UserAgent` | `GrayMint.Webhooks` | Of every request. |
| `AllowInsecureTargets` | `false` | For development and tests alone: http, this machine and private networks. |

`WebhookMessageCreateRequest.Lifetime` gives a message less time than its attempts take: one that is not delivered within it is given up.

## Requirements

.NET 11, and PostgreSQL or SQL Server through your own Entity Framework Core provider. The module names none.

- **SQL Server**: turn `READ_COMMITTED_SNAPSHOT` on for the database. Without it, a transaction of yours that has queued a message keeps every host's look for due messages waiting until it ends.
- **Clocks**: hosts that deliver keep their clocks together, as servers do. A hold on a message is counted by the clock of the host that took it, and a host whose clock runs 20 seconds ahead of another's could send a message the other is still sending.
- **Your subscribers' lookup** (`IWebhookSubscribers.Find`) is quick: a message is held for its delivery meanwhile. One that fails, or takes more than 10 seconds, costs the message no attempt: it waits a minute.

## License

MIT
