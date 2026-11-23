using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMint.Webhooks.Test.Tests;

// The module on the host's own connection: a message is queued in the transaction the host has open.
[TestClass]
public class HostTransactionTest(TestContext testContext)
{
    private static readonly Uri Url = new("https://shop.example.com/webhooks");

    private CancellationToken CancellationToken => testContext.CancellationToken;

    private static WebhookMessageCreateRequest Request()
    {
        return new WebhookMessageCreateRequest {
            SubscriberId = TestWebhookHost.SubscriberId, Url = Url, EventType = "order.paid", Payload = TestWebhookHost.Payload
        };
    }

    private async Task<int> CountMessages(TestWebhookHost host)
    {
        await using var dbContext = host.CreateDbContext();
        return await dbContext.Messages.CountAsync(CancellationToken);
    }

    private async Task<int> CountOrders(TestWebhookHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestShopDbContext>().Orders.CountAsync(CancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_commits_with_the_hosts_transaction(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, onHostConnection: true);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
            await using var transaction = await shopDbContext.Database.BeginTransactionAsync(CancellationToken);
            shopDbContext.Orders.Add(new TestOrder { State = "paid" });
            await shopDbContext.SaveChangesAsync(CancellationToken);
            var message = await service.Create(Request(), CancellationToken);

            // the transaction's own reads see what it wrote
            Assert.AreEqual(message.MessageId, (await service.Get(TestWebhookHost.SubscriberId, message.MessageId, CancellationToken)).MessageId);
            await transaction.CommitAsync(CancellationToken);
        }

        Assert.AreEqual(1, await CountOrders(host));
        Assert.AreEqual(1, await CountMessages(host));
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_goes_with_the_hosts_transaction_that_is_rolled_back(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, onHostConnection: true);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
            await using var transaction = await shopDbContext.Database.BeginTransactionAsync(CancellationToken);
            shopDbContext.Orders.Add(new TestOrder { State = "paid" });
            await shopDbContext.SaveChangesAsync(CancellationToken);
            await service.Create(Request(), CancellationToken);
            await transaction.RollbackAsync(CancellationToken);

            // the scope goes on after a transaction that is over, in none
            await service.Create(Request(), CancellationToken);
        }

        Assert.AreEqual(0, await CountOrders(host));
        Assert.AreEqual(1, await CountMessages(host), "only the message queued after the roll back is there");
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_that_fails_to_be_written_leaves_the_hosts_transaction_usable(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, onHostConnection: true);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
            await using var transaction = await shopDbContext.Database.BeginTransactionAsync(CancellationToken);
            shopDbContext.Orders.Add(new TestOrder { State = "paid" });
            await shopDbContext.SaveChangesAsync(CancellationToken);

            // The message's row is refused by the database itself. On PostgreSQL that ends a transaction, unless it
            // is put back to a savepoint taken before the write.
            host.Commands.BeforeCommand = command => {
                command.CommandText = command.CommandText.Replace(WebhooksSchema.MessagesTable, "NoSuchTable", StringComparison.Ordinal);
                return Task.CompletedTask;
            };

            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => service.Create(Request(), CancellationToken));
            host.Commands.BeforeCommand = null;

            // the host goes on in its transaction: it writes, queues again, and commits
            shopDbContext.Orders.Add(new TestOrder { State = "sent" });
            await shopDbContext.SaveChangesAsync(CancellationToken);
            await service.Create(Request(), CancellationToken);
            await transaction.CommitAsync(CancellationToken);
        }

        Assert.AreEqual(2, await CountOrders(host));
        Assert.AreEqual(1, await CountMessages(host), "only the message that was written is there");
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_is_delivered_after_the_hosts_transaction_commits(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, onHostConnection: true);
        await using var receiver = await TestReceiver.Start(CancellationToken);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            await using var transaction = await shopDbContext.Database.BeginTransactionAsync(CancellationToken);
            await scope.ServiceProvider.GetRequiredService<WebhookMessageService>().Create(
                new WebhookMessageCreateRequest {
                    SubscriberId = TestWebhookHost.SubscriberId, Url = receiver.Url("/webhooks"), EventType = "order.paid",
                    Payload = TestWebhookHost.Payload
                }, CancellationToken);

            await transaction.CommitAsync(CancellationToken);
        }

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.SucceededCount);
        Assert.AreEqual(1, receiver.Requests.Count);
    }
}
