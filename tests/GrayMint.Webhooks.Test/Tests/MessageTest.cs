using GrayMint.Common.Exceptions;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class MessageTest(TestContext testContext)
{
    private static readonly Uri Url = new("https://shop.example.com/webhooks");

    private CancellationToken CancellationToken => testContext.CancellationToken;

    private static WebhookMessageCreateRequest Request(string subscriberId = TestWebhookHost.SubscriberId, Uri? url = null,
        string eventType = "order.paid", string payload = TestWebhookHost.Payload, string? subject = null,
        TimeSpan? lifetime = null)
    {
        return new WebhookMessageCreateRequest {
            SubscriberId = subscriberId, Url = url ?? Url, EventType = eventType, Payload = payload, Subject = subject,
            Lifetime = lifetime
        };
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Create_queues_a_pending_message_that_is_due_at_once(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;

        var created = await host.Queue(Url, CancellationToken, subject: "order-1001", lifetime: TimeSpan.FromMinutes(15));

        var message = await host.Get(created.MessageId, CancellationToken);
        StringAssert.Matches(message.MessageId, new System.Text.RegularExpressions.Regex("^msg_[0-9a-f]{32}$"));
        Assert.AreEqual('7', message.MessageId[16], "the id is made of a UUID of version 7");
        Assert.AreEqual(TestWebhookHost.SubscriberId, message.SubscriberId);
        Assert.AreEqual("order-1001", message.Subject);
        Assert.AreEqual("order.paid", message.EventType);
        Assert.AreEqual(Url, message.Url);
        Assert.AreEqual(TestWebhookHost.Payload, message.Payload);
        Assert.AreEqual(WebhookMessageStatus.Pending, message.Status);
        Assert.AreEqual(0, message.AttemptCount);
        AssertTime(now, message.CreatedTime);
        AssertTime(now, message.NextAttemptTime);
        AssertTime(now + TimeSpan.FromMinutes(15), message.ExpirationTime);
        Assert.AreEqual(DateTimeKind.Utc, message.CreatedTime.Kind);
        Assert.IsNull(message.CompletedTime);
        Assert.IsNull(message.LastError);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Create_refuses_a_value_that_is_not_as_the_request_says_and_keeps_nothing(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var refused = new (WebhookMessageCreateRequest Request, string Message)[] {
            (Request(subscriberId: ""), "A subscriber's id has 1 to 100 characters."),
            (Request(subscriberId: new string('a', 101)), "A subscriber's id has 1 to 100 characters."),
            (Request(eventType: ""), "An event type has 1 to 100 characters."),
            (Request(payload: ""), "A payload has 1 to 262144 characters."),
            (Request(payload: new string('a', 262145)), "A payload has 1 to 262144 characters."),
            (Request(subject: ""), "A subject has 1 to 200 characters."),
            (Request(subject: new string('a', 201)), "A subject has 1 to 200 characters."),
            (Request(subscriberId: "shop-1 "), "A subscriber's id does not end in white space."),
            (Request(subscriberId: "shop-1\t"), "A subscriber's id does not end in white space."),
            (Request(subject: "order-1 "), "A subject does not end in white space."),
            (Request(subscriberId: "shop\0-1"), "A subscriber's id has no NUL character."),
            (Request(subject: "order\0-1"), "A subject has no NUL character."),
            (Request(eventType: "order\0paid"), "An event type has no NUL character."),
            (Request(payload: "{\"note\":\"\0\"}"), "A payload has no NUL character."),
            (Request(url: new Uri("/webhooks", UriKind.Relative)), "A webhook goes to an absolute http or https address."),
            (Request(url: new Uri("ftp://shop.example.com/webhooks")), "A webhook goes to an absolute http or https address."),
            (Request(url: new Uri("https://user:secret@shop.example.com/")), "A webhook's address carries no user name or password."),
            (Request(url: new Uri("https://shop.example.com/" + new string('a', 2048))), "A webhook's address has up to 2048 characters."),
            (Request(lifetime: TimeSpan.Zero), "A message's lifetime is a millisecond or more, or null."),
            (Request(lifetime: TimeSpan.FromTicks(9999)), "A message's lifetime is a millisecond or more, or null.")
        };

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
        foreach (var (request, message) in refused) {
            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.Create(request, CancellationToken));
            StringAssert.StartsWith(exception.Message, message);
        }

        await using var dbContext = host.CreateDbContext();
        Assert.AreEqual(0, await dbContext.Messages.CountAsync(CancellationToken));
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_is_its_subscribers_alone_whatever_the_case_of_an_id(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var mine = await host.Queue(Url, CancellationToken, subscriberId: "shop-1");
        var others = await host.Queue(Url, CancellationToken, subscriberId: "Shop-1");

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
        var exception = await Assert.ThrowsExactlyAsync<NotExistsException>(
            () => service.Get("shop-1", others.MessageId, CancellationToken));

        StringAssert.Contains(exception.Message, others.MessageId);
        await Assert.ThrowsExactlyAsync<NotExistsException>(() => service.GetAttempts("shop-1", others.MessageId, CancellationToken));
        await Assert.ThrowsExactlyAsync<NotExistsException>(() => service.Retry("shop-1", others.MessageId, CancellationToken));

        var messages = await service.GetBySubscriber("shop-1", null, null, 10, CancellationToken);
        CollectionAssert.AreEqual(new[] { mine.MessageId }, messages.Select(x => x.MessageId).ToArray());
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Id_of_another_form_names_no_message(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();

        foreach (var messageId in new[] { "", "17", "msg_", "msg_zz", Guid.CreateVersion7().ToString("N"), "MSG_" + Guid.CreateVersion7().ToString("N") }) {
            var exception = await Assert.ThrowsExactlyAsync<NotExistsException>(
                () => service.Get(TestWebhookHost.SubscriberId, messageId, CancellationToken));

            Assert.AreEqual($"Could not find the message. Id: {messageId}", exception.Message);
        }
    }

    [TestMethod]
    [EveryDatabase]
    public async Task GetBySubscriber_pages_the_newest_first_and_by_subject(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var first = await host.Queue(Url, CancellationToken, subject: "order-1");
        var second = await host.Queue(Url, CancellationToken, subject: "order-2");
        var third = await host.Queue(Url, CancellationToken, subject: "order-1");
        var fourth = await host.Queue(Url, CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
        var page1 = await service.GetBySubscriber(TestWebhookHost.SubscriberId, null, null, 3, CancellationToken);
        var page2 = await service.GetBySubscriber(TestWebhookHost.SubscriberId, null, page1[^1].MessageId, 3, CancellationToken);
        var ofSubject = await service.GetBySubscriber(TestWebhookHost.SubscriberId, "order-1", null, 10, CancellationToken);

        CollectionAssert.AreEqual(new[] { fourth.MessageId, third.MessageId, second.MessageId }, page1.Select(x => x.MessageId).ToArray());
        CollectionAssert.AreEqual(new[] { first.MessageId }, page2.Select(x => x.MessageId).ToArray());
        CollectionAssert.AreEqual(new[] { third.MessageId, first.MessageId }, ofSubject.Select(x => x.MessageId).ToArray());
        foreach (var limit in new[] { 0, WebhookLimits.MaxPageSize + 1 }) {
            var exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => service.GetBySubscriber(TestWebhookHost.SubscriberId, null, null, limit, CancellationToken));

            StringAssert.StartsWith(exception.Message, "A page has 1 to 100 messages.");
        }
    }

    // SQL Server compares a text as if the spaces at its end were not there, and PostgreSQL does not
    [TestMethod]
    [EveryDatabase]
    public async Task Id_or_subject_that_ends_in_white_space_names_nothing(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var message = await host.Queue(Url, CancellationToken, subject: "order-1");

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
        var exception = await Assert.ThrowsExactlyAsync<NotExistsException>(
            () => service.Get("shop-1 ", message.MessageId, CancellationToken));

        Assert.AreEqual($"Message {message.MessageId} is not the subscriber's. SubscriberId: shop-1 ", exception.Message);
        await Assert.ThrowsExactlyAsync<NotExistsException>(() => service.GetAttempts("shop-1 ", message.MessageId, CancellationToken));
        await Assert.ThrowsExactlyAsync<NotExistsException>(() => service.Retry("shop-1 ", message.MessageId, CancellationToken));
        await Assert.ThrowsExactlyAsync<NotExistsException>(
            () => service.GetBySubscriber("shop-1 ", null, message.MessageId, 10, CancellationToken));

        Assert.AreEqual(0, (await service.GetBySubscriber("shop-1 ", null, null, 10, CancellationToken)).Count);
        Assert.AreEqual(0, (await service.GetBySubscriber("shop-1", "order-1 ", null, 10, CancellationToken)).Count);
        Assert.AreEqual(0, await service.DeleteBySubscriber("shop-1 ", CancellationToken));

        // the subscriber's message is as it was, and found by its exact id and subject
        var found = await service.GetBySubscriber("shop-1", "order-1", null, 10, CancellationToken);
        CollectionAssert.AreEqual(new[] { message.MessageId }, found.Select(x => x.MessageId).ToArray());
    }

    [TestMethod]
    [EveryDatabase]
    public async Task DeleteBySubscriber_removes_its_messages_with_their_attempts_and_no_others(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        host.Subscribers.Set("shop-2");
        await host.Queue(receiver.Url("/a"), CancellationToken);
        await host.Queue(receiver.Url("/b"), CancellationToken);
        var others = await host.Queue(receiver.Url("/c"), CancellationToken, subscriberId: "shop-2");
        await host.Deliver(CancellationToken);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var deleted = await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
                .DeleteBySubscriber(TestWebhookHost.SubscriberId, CancellationToken);

            Assert.AreEqual(2, deleted);
        }

        await using var dbContext = host.CreateDbContext();
        Assert.AreEqual(1, await dbContext.Messages.CountAsync(CancellationToken));
        Assert.AreEqual(1, await dbContext.Attempts.CountAsync(CancellationToken));
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(others.MessageId, CancellationToken, "shop-2")).Status);
    }

    // a database keeps a time less finely than the clock gives it
    private static void AssertTime(DateTime expected, DateTime? actual)
    {
        Assert.IsNotNull(actual);
        Assert.IsTrue((expected - actual.Value).Duration() < TimeSpan.FromMilliseconds(1), $"Expected {expected:O}, got {actual:O}.");
    }
}
