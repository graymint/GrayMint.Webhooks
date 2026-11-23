using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;

namespace GrayMint.Webhooks.Test.Tests;

// A subscriber's messages of one subject go in the order they were queued.
[TestClass]
public class OrderTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    private Task<TestWebhookHost> CreateHost(TestDatabaseProvider provider, bool onHostConnection,
        Action<WebhookDeliveryOptions>? configureDelivery = null)
    {
        return TestWebhookHost.Create(provider, CancellationToken, configureDelivery, onHostConnection);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Failed_message_keeps_the_later_ones_of_its_subject_waiting_until_it_is_delivered(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.SetStatusCode("/first", 500);
        await host.Queue(receiver.Url("/first"), CancellationToken, subject: "order-1");
        var second = await host.Queue(receiver.Url("/second"), CancellationToken, subject: "order-1");
        await host.Queue(receiver.Url("/third"), CancellationToken, subject: "order-1");

        await host.Deliver(CancellationToken);

        CollectionAssert.AreEqual(new[] { "/first" }, receiver.Requests.Select(x => x.Path).ToArray());
        Assert.AreEqual(0, (await host.Get(second.MessageId, CancellationToken)).AttemptCount, "it is due, and waits");

        // the first one's retry is taken: the others follow in the same run, each after the one before, though a
        // first attempt goes before a retry where no subject orders them
        host.Time.Advance(TimeSpan.FromSeconds(5));
        receiver.SetStatusCode("/first", 200);
        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(3, result.SucceededCount);
        CollectionAssert.AreEqual(new[] { "/first", "/first", "/second", "/third" }, receiver.Requests.Select(x => x.Path).ToArray());
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Messages_of_another_subject_of_none_or_of_another_subscriber_do_not_wait(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        host.Subscribers.Set("shop-2");
        receiver.SetStatusCode("/failing", 500);
        await host.Queue(receiver.Url("/failing"), CancellationToken, subject: "order-1");
        await host.Queue(receiver.Url("/failing"), CancellationToken);
        await host.Queue(receiver.Url("/other-subject"), CancellationToken, subject: "order-2");
        await host.Queue(receiver.Url("/no-subject"), CancellationToken);
        await host.Queue(receiver.Url("/other-subscriber"), CancellationToken, subject: "order-1", subscriberId: "shop-2");

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(3, result.SucceededCount);
        Assert.AreEqual(2, result.FailedCount);
        CollectionAssert.AreEquivalent(
            new[] { "/failing", "/failing", "/other-subject", "/no-subject", "/other-subscriber" },
            receiver.Requests.Select(x => x.Path).ToArray());
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_that_is_given_up_lets_the_later_ones_of_its_subject_go(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.RetryDelays = []);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.SetStatusCode("/first", 500);
        var first = await host.Queue(receiver.Url("/first"), CancellationToken, subject: "order-1");
        var second = await host.Queue(receiver.Url("/second"), CancellationToken, subject: "order-1");

        await host.Deliver(CancellationToken);

        Assert.AreEqual(WebhookMessageStatus.Failed, (await host.Get(first.MessageId, CancellationToken)).Status);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(second.MessageId, CancellationToken)).Status);
        CollectionAssert.AreEqual(new[] { "/first", "/second" }, receiver.Requests.Select(x => x.Path).ToArray());
    }
}
