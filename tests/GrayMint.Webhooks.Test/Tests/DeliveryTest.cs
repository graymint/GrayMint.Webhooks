using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class DeliveryTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    private Task<TestWebhookHost> CreateHost(TestDatabaseProvider provider, bool onHostConnection,
        Action<WebhookDeliveryOptions>? configureDelivery = null)
    {
        return TestWebhookHost.Create(provider, CancellationToken, configureDelivery, onHostConnection);
    }

    private static void AssertTime(DateTime expected, DateTime? actual)
    {
        Assert.IsNotNull(actual);
        Assert.IsTrue((expected - actual.Value).Duration() < TimeSpan.FromMilliseconds(1), $"Expected {expected:O}, got {actual:O}.");
    }

    // a machine may name an address of a family it has turned off
    private static bool CanListenOn(IPAddress address)
    {
        try {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException) {
            return false;
        }
    }

    // the signature of "<id>.<time>.<body>" as a receiver computes it from what it got, under a key given in base64
    private static string Sign(TestReceivedRequest request, string base64Key)
    {
        var signed = Encoding.UTF8.GetBytes($"{request.Headers["webhook-id"]}.{request.Headers["webhook-timestamp"]}.{request.Body}");
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(base64Key), signed));
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Due_message_is_posted_signed_with_its_headers_and_marked_succeeded(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.UserAgent = "Shop-Webhooks/1.0");
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks?shop=1"), CancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(3));
        var now = host.Time.GetUtcNow();

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.SucceededCount);
        Assert.AreEqual(0, result.FailedCount);
        var request = receiver.Requests.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/webhooks", request.Path);
        Assert.AreEqual(TestWebhookHost.Payload, request.Body);
        Assert.AreEqual("application/json; charset=utf-8", request.Headers["Content-Type"]);
        Assert.AreEqual("Shop-Webhooks/1.0", request.Headers["User-Agent"]);
        Assert.AreEqual(message.MessageId, request.Headers["webhook-id"]);
        Assert.AreEqual(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), request.Headers["webhook-timestamp"]);
        Assert.AreEqual(Sign(request, "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"), request.Headers["webhook-signature"]);

        var delivered = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, delivered.Status);
        Assert.AreEqual(1, delivered.AttemptCount);
        Assert.AreEqual(200, delivered.LastStatusCode);
        Assert.IsNull(delivered.LastError);
        Assert.IsNull(delivered.NextAttemptTime);
        AssertTime(now.UtcDateTime, delivered.CompletedTime);
        AssertTime(now.UtcDateTime, delivered.LastAttemptTime);

        var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
        Assert.IsTrue(attempt.IsSuccess);
        Assert.AreEqual(200, attempt.StatusCode);
        Assert.IsNull(attempt.Error);
        AssertTime(now.UtcDateTime, attempt.AttemptTime);

        var again = await host.Deliver(CancellationToken);
        Assert.AreEqual(0, again.SucceededCount + again.FailedCount);
        Assert.AreEqual(1, receiver.Requests.Count, "a delivered message goes once");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_is_signed_with_each_secret_of_its_subscriber(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);

        // the current one as the specification writes a secret, and an old one without whsec_ before it
        host.Subscribers.Set(TestWebhookHost.SubscriberId, new WebhookSubscriber {
            SigningSecrets = ["whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "b2xkLXNlY3JldC1vZi10aGUtc2hvcA=="]
        });

        await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        await host.Deliver(CancellationToken);

        var request = receiver.Requests.Single();
        Assert.AreEqual(
            $"{Sign(request, "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")} {Sign(request, "b2xkLXNlY3JldC1vZi10aGUtc2hvcA==")}",
            request.Headers["webhook-signature"]);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Subscribers_headers_go_with_the_message_but_never_in_place_of_the_senders_own(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        host.Subscribers.Set(TestWebhookHost.SubscriberId, new WebhookSubscriber {
            SigningSecrets = [TestSubscribers.Secret],
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer shop-token", ["X-Shop"] = "1" }
        });

        await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        await host.Deliver(CancellationToken);
        var request = receiver.Requests.Single();
        Assert.AreEqual("Bearer shop-token", request.Headers["Authorization"]);
        Assert.AreEqual("1", request.Headers["X-Shop"]);

        // Host among them: it would send the request to another site of the receiver's server
        foreach (var name in new[] { "Webhook-Id", "webhook-timestamp", "Webhook-Signature", "user-agent", "Host" }) {
            host.Subscribers.Set(TestWebhookHost.SubscriberId, new WebhookSubscriber {
                SigningSecrets = [TestSubscribers.Secret], Headers = new Dictionary<string, string> { [name] = "forged" }
            });

            var refused = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
            await host.Deliver(CancellationToken);

            Assert.AreEqual(1, receiver.Requests.Count, $"the forged {name} never left");
            var attempt = (await host.GetAttempts(refused.MessageId, CancellationToken)).Single();
            Assert.IsFalse(attempt.IsSuccess);
            Assert.AreEqual($"A subscriber's header can't be {name}, which is the sender's own.", attempt.Error);
        }
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Failed_attempt_keeps_its_answer_and_the_message_goes_again_after_its_wait(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 500;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);
        var failed = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Pending, failed.Status);
        Assert.AreEqual(1, failed.AttemptCount);
        Assert.AreEqual(500, failed.LastStatusCode);
        Assert.AreEqual("The receiver answered 500 (InternalServerError).", failed.LastError);
        AssertTime(now + TimeSpan.FromSeconds(5), failed.NextAttemptTime);
        Assert.IsNull(failed.CompletedTime);

        // not before its wait is over
        host.Time.Advance(TimeSpan.FromSeconds(4));
        await host.Deliver(CancellationToken);
        Assert.AreEqual(1, receiver.Requests.Count);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        receiver.StatusCode = 200;
        await host.Deliver(CancellationToken);

        Assert.AreEqual(2, receiver.Requests.Count);
        Assert.AreEqual(receiver.Requests[0].Headers["webhook-id"], receiver.Requests[1].Headers["webhook-id"], "a retry has the message's id");
        var delivered = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, delivered.Status);
        Assert.AreEqual(2, delivered.AttemptCount);
        Assert.IsNull(delivered.LastError);
        var attempts = await host.GetAttempts(message.MessageId, CancellationToken);
        CollectionAssert.AreEqual(new[] { false, true }, attempts.Select(x => x.IsSuccess).ToArray());
        CollectionAssert.AreEqual(new int?[] { 500, 200 }, attempts.Select(x => x.StatusCode).ToArray());
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_is_given_up_after_its_last_attempt(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.RetryDelays = [TimeSpan.FromSeconds(1)]);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 503;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        await host.Deliver(CancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var now = host.Time.GetUtcNow().UtcDateTime;
        await host.Deliver(CancellationToken);

        var givenUp = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, givenUp.Status);
        Assert.AreEqual(2, givenUp.AttemptCount);
        Assert.AreEqual("The receiver answered 503 (ServiceUnavailable).", givenUp.LastError);
        Assert.IsNull(givenUp.NextAttemptTime);
        AssertTime(now, givenUp.CompletedTime);

        host.Time.Advance(TimeSpan.FromHours(1));
        await host.Deliver(CancellationToken);
        Assert.AreEqual(2, receiver.Requests.Count, "a message that is given up goes no more");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Receiver_that_answers_gone_gets_the_message_no_more(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 410;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        var givenUp = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, givenUp.Status);
        Assert.AreEqual(1, givenUp.AttemptCount);
        Assert.AreEqual("The receiver answered 410 (Gone).", givenUp.LastError);
        Assert.AreEqual(410, (await host.GetAttempts(message.MessageId, CancellationToken)).Single().StatusCode);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Redirect_is_an_answer_and_is_never_followed(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 302;
        receiver.RedirectLocation = receiver.Url("/inside").AbsoluteUri;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        await host.Deliver(CancellationToken);

        CollectionAssert.AreEqual(new[] { "/webhooks" }, receiver.Requests.Select(x => x.Path).ToArray());
        var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
        Assert.IsFalse(attempt.IsSuccess);
        Assert.AreEqual(302, attempt.StatusCode);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Receiver_that_does_not_answer_in_time_fails_the_attempt(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection,
            options => options.AttemptTimeout = TimeSpan.FromMilliseconds(300));

        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.Delay = TimeSpan.FromSeconds(30);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        await host.Deliver(CancellationToken);

        var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
        Assert.IsFalse(attempt.IsSuccess);
        Assert.IsNull(attempt.StatusCode);
        Assert.AreEqual("The receiver did not answer within 0.3 seconds.", attempt.Error);
        Assert.AreEqual(WebhookMessageStatus.Pending, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Receiver_that_cannot_be_reached_fails_the_attempt(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);

        // a port that is taken and takes no connection: no other test's receiver gets it meanwhile
        using var closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var message = await host.Queue(new Uri($"http://{closed.LocalEndPoint}/webhooks"), CancellationToken);

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
        Assert.IsFalse(attempt.IsSuccess);
        Assert.IsNull(attempt.StatusCode);
        Assert.IsFalse(string.IsNullOrEmpty(attempt.Error));
        Assert.AreEqual(WebhookMessageStatus.Pending, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Name_with_several_addresses_is_tried_at_each_until_one_takes_the_connection(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        var addresses = await Dns.GetHostAddressesAsync("localhost", CancellationToken);
        if (addresses.Select(x => x.AddressFamily).Distinct().Count() < 2 || !CanListenOn(addresses[^1]))
            Assert.Inconclusive("The name localhost has no two addresses to listen on here, so there is no second one to go on to.");

        // the receiver listens on the name's last address alone, so its first one takes no connection
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(addresses[^1], CancellationToken);

        var message = await host.Queue(new Uri($"http://localhost:{receiver.BaseUri.Port}/webhooks"), CancellationToken);
        await host.Deliver(CancellationToken);

        Assert.AreEqual(1, receiver.Requests.Count);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Expired_message_is_given_up_without_a_request(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken, lifetime: TimeSpan.FromMinutes(1));
        host.Time.Advance(TimeSpan.FromMinutes(1));

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(0, receiver.Requests.Count);
        var expired = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, expired.Status);
        Assert.AreEqual(0, expired.AttemptCount);
        Assert.AreEqual("The message expired before it was delivered.", expired.LastError);
        Assert.AreEqual(0, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_whose_next_attempt_would_come_after_its_expiry_is_given_up_at_once(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection,
            options => options.RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50)]);

        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 500;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken, lifetime: TimeSpan.FromSeconds(60));

        // the second attempt, 10 seconds on, comes before the expiry; the third, 60 seconds on, would not
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Pending, (await host.Get(message.MessageId, CancellationToken)).Status);
        host.Time.Advance(TimeSpan.FromSeconds(10));
        await host.Deliver(CancellationToken);

        var givenUp = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, givenUp.Status);
        Assert.AreEqual(2, givenUp.AttemptCount);
        Assert.AreEqual("The receiver answered 500 (InternalServerError).", givenUp.LastError);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_of_a_subscriber_that_is_no_more_is_given_up_without_a_request(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        host.Subscribers.Remove(TestWebhookHost.SubscriberId);

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(0, receiver.Requests.Count);
        var givenUp = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, givenUp.Status);
        Assert.AreEqual("The subscriber is no more.", givenUp.LastError);
        Assert.AreEqual(0, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_waits_and_loses_no_attempt_while_the_host_cannot_look_up_its_subscriber(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;
        var failure = new InvalidOperationException("Login failed for the shop's database at db-1.internal.");
        host.Subscribers.Failure = failure;

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount + result.FailedCount);
        Assert.AreEqual(0, receiver.Requests.Count);
        var waiting = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Pending, waiting.Status);
        Assert.AreEqual(0, waiting.AttemptCount);
        AssertTime(now + TimeSpan.FromMinutes(1), waiting.NextAttemptTime);
        Assert.AreEqual(0, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);

        // the host's own failure is the log's alone: a host may show a message's error to its subscriber
        Assert.AreEqual("The host could not look up the message's subscriber; the message waits.", waiting.LastError);
        var logged = host.Log.Entries.Single(x => x.Level == LogLevel.Error);
        Assert.AreSame(failure, logged.Exception);

        host.Subscribers.Failure = null;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.Deliver(CancellationToken);

        var delivered = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, delivered.Status);
        Assert.AreEqual(1, delivered.AttemptCount);
        Assert.IsNull(delivered.LastError);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_waits_when_the_lookup_of_its_subscriber_does_not_end(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection,
            options => options.StartTimeout = TimeSpan.FromMilliseconds(300));

        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;

        // as on a database that takes no command: the lookup ends by its token alone
        host.Subscribers.BeforeAnswer = token => Task.Delay(Timeout.Infinite, token);
        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount + result.FailedCount);
        await AssertWaiting(host, receiver, message, now, "The host could not look up the message's subscriber; the message waits.");

        host.Subscribers.BeforeAnswer = null;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_waits_when_its_lookup_answers_only_after_the_time_its_hold_leaves(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection,
            options => options.StartTimeout = TimeSpan.FromMilliseconds(300));

        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;

        // a lookup that goes on past its token: a request begun so late could be under way when the hold is over
        host.Subscribers.BeforeAnswer = _ => Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken);
        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount + result.FailedCount);
        await AssertWaiting(host, receiver, message, now, "The host took too long to get the message ready; the message waits.");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_waits_when_its_claim_took_longer_than_the_time_its_hold_leaves(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection,
            options => options.StartTimeout = TimeSpan.FromMilliseconds(300));

        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;

        // as a claim that waited for another transaction to let go of the row: the hold is counted from before it
        host.Commands.BeforeCommand = command => TestCommandInterceptor.IsClaim(command)
            ? Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken)
            : Task.CompletedTask;

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount + result.FailedCount);
        await AssertWaiting(host, receiver, message, now, "The host took too long to get the message ready; the message waits.");

        host.Commands.BeforeCommand = null;
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    // no request was made, and the message is as it was but for its wait of a minute and what it says of it
    private async Task AssertWaiting(TestWebhookHost host, TestReceiver receiver, WebhookMessage message, DateTime now,
        string error)
    {
        Assert.AreEqual(0, receiver.Requests.Count);
        var waiting = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Pending, waiting.Status);
        Assert.AreEqual(0, waiting.AttemptCount);
        Assert.AreEqual(error, waiting.LastError);
        AssertTime(now + TimeSpan.FromMinutes(1), waiting.NextAttemptTime);
        Assert.AreEqual(0, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);
        await using var dbContext = host.CreateDbContext();
        Assert.IsNull((await dbContext.Messages.AsNoTracking().SingleAsync(CancellationToken)).LockToken, "it is held no more");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Retry_puts_a_failed_message_back_with_its_attempts_ahead_and_its_first_lifetime(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.RetryDelays = []);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 500;
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken, lifetime: TimeSpan.FromMinutes(1));

        // twice: each time it lives as long as it was queued to, not as long as the retry before left it
        for (var i = 0; i < 2; i++) {
            await host.Deliver(CancellationToken);
            Assert.AreEqual(WebhookMessageStatus.Failed, (await host.Get(message.MessageId, CancellationToken)).Status);
            host.Time.Advance(TimeSpan.FromMinutes(10));
            var now = host.Time.GetUtcNow().UtcDateTime;

            await using var scope = host.Services.CreateAsyncScope();
            var retried = await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
                .Retry(TestWebhookHost.SubscriberId, message.MessageId, CancellationToken);

            Assert.AreEqual(WebhookMessageStatus.Pending, retried.Status);
            Assert.AreEqual(0, retried.AttemptCount);
            Assert.IsNull(retried.CompletedTime);
            AssertTime(now, retried.NextAttemptTime);
            AssertTime(now + TimeSpan.FromMinutes(1), retried.ExpirationTime);
        }

        receiver.StatusCode = 200;
        await host.Deliver(CancellationToken);

        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
        Assert.AreEqual(3, (await host.GetAttempts(message.MessageId, CancellationToken)).Count, "the history keeps the attempts before");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Retry_refuses_a_message_that_has_not_failed_and_leaves_it_as_it_is(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var pending = await host.Queue(receiver.Url("/later"), CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookMessageService>();
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.Retry(TestWebhookHost.SubscriberId, pending.MessageId, CancellationToken));

        Assert.AreEqual($"Only a failed message is put back, and message {pending.MessageId} has not failed.", exception.Message);

        await host.Deliver(CancellationToken);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.Retry(TestWebhookHost.SubscriberId, pending.MessageId, CancellationToken));

        var delivered = await host.Get(pending.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, delivered.Status);
        Assert.AreEqual(1, delivered.AttemptCount);
        await host.Deliver(CancellationToken);
        Assert.AreEqual(1, receiver.Requests.Count, "a delivered message is not sent again");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Subscriber_whose_secrets_cannot_sign_fails_the_attempt_and_nothing_goes_unsigned(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        const string notBase64 = "A signing secret of the subscriber is empty, or is not base64.";
        var refused = new (IReadOnlyList<string> Secrets, string Error)[] {
            ([], "The subscriber has no signing secret, and a message never goes unsigned."),
            ([""], notBase64),
            (["whsec_"], notBase64),
            (["a secret, in plain text"], notBase64),
            ([TestSubscribers.Secret, "whsec_"], notBase64)
        };

        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        foreach (var (secrets, error) in refused) {
            host.Subscribers.Set(TestWebhookHost.SubscriberId, new WebhookSubscriber { SigningSecrets = secrets });
            var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

            await host.Deliver(CancellationToken);

            var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
            Assert.IsFalse(attempt.IsSuccess);
            Assert.AreEqual(error, attempt.Error);
        }

        Assert.AreEqual(0, receiver.Requests.Count);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Error_longer_than_the_history_keeps_is_cut_and_written(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);

        // a header's name that can't be one: the failure repeats it in whole, and a column takes no more than it has
        host.Subscribers.Set(TestWebhookHost.SubscriberId, new WebhookSubscriber {
            SigningSecrets = [TestSubscribers.Secret],
            Headers = new Dictionary<string, string> { [new string('x', 1500) + " y"] = "1" }
        });

        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        var error = (await host.GetAttempts(message.MessageId, CancellationToken)).Single().Error;
        Assert.IsNotNull(error);
        Assert.AreEqual(1000, error.Length);
        Assert.AreEqual(error, (await host.Get(message.MessageId, CancellationToken)).LastError);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Removal_of_a_subscribers_messages_while_one_is_being_sent_fails_no_run_and_no_other_message(
        TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        host.Subscribers.Set("shop-2");
        await host.Queue(receiver.Url("/removed"), CancellationToken);
        var kept = await host.Queue(receiver.Url("/kept"), CancellationToken, subscriberId: "shop-2");

        // both requests are under way when the first subscriber's messages are removed
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BeforeAnswer = async request => {
            if (request.Path != "/removed") {
                await removed.Task;
                return;
            }

            await TestWait.Until(() => receiver.Requests.Count == 2, "both requests are under way", CancellationToken);
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
                .DeleteBySubscriber(TestWebhookHost.SubscriberId, CancellationToken);

            removed.SetResult();
        };

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.SucceededCount, "the other subscriber's message is delivered");
        Assert.AreEqual(0, result.FailedCount);
        Assert.AreEqual(2, receiver.Requests.Count, "and no request was made twice");
        var delivered = await host.Get(kept.MessageId, CancellationToken, "shop-2");
        Assert.AreEqual(WebhookMessageStatus.Succeeded, delivered.Status);
        Assert.AreEqual(1, delivered.AttemptCount);
        Assert.IsTrue(host.Log.Entries.Any(x => x.Level == LogLevel.Warning &&
            x.Message.StartsWith("A webhook's outcome was not written, as its delivery held it no more.", StringComparison.Ordinal)));

        await using var dbContext = host.CreateDbContext();
        Assert.AreEqual(1, await dbContext.Messages.CountAsync(CancellationToken));
        Assert.AreEqual(1, await dbContext.Attempts.CountAsync(CancellationToken), "nothing is kept of the removed message's attempt");
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task First_attempt_goes_before_a_retry_that_has_been_due_for_longer(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.MaxConcurrentDeliveries = 1);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.SetStatusCode("/retried", 500);
        await host.Queue(receiver.Url("/retried"), CancellationToken);
        await host.Deliver(CancellationToken);

        // the retry has been due for a second when the new message is queued
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await host.Queue(receiver.Url("/new"), CancellationToken);
        receiver.SetStatusCode("/retried", 200);
        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(2, result.SucceededCount);
        CollectionAssert.AreEqual(new[] { "/retried", "/new", "/retried" }, receiver.Requests.Select(x => x.Path).ToArray());
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_queued_during_a_run_goes_before_the_retries_an_earlier_look_found(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection, options => options.MaxConcurrentDeliveries = 1);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.StatusCode = 500;
        await host.Queue(receiver.Url("/a"), CancellationToken);
        await host.Queue(receiver.Url("/b"), CancellationToken);
        await host.Queue(receiver.Url("/c"), CancellationToken);
        await host.Deliver(CancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(5));
        receiver.StatusCode = 200;

        // While the first retry is under way a message is queued, and the retry takes longer than a look's list is
        // handed out for: the slot it frees goes to the new message, and not to the next retry of that list.
        receiver.BeforeAnswer = async _ => {
            if (receiver.Requests.Count != 4)
                return;

            await host.Queue(receiver.Url("/new"), CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken);
        };

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(4, result.SucceededCount);
        CollectionAssert.AreEqual(new[] { "/a", "/b", "/c", "/a", "/new", "/b", "/c" },
            receiver.Requests.Select(x => x.Path).ToArray());
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Run_that_fails_cuts_off_no_turn_under_way(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        // one message's claim fails, as on a database that is gone for a moment, while the other's request is under way
        var claimCount = 0;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Commands.BeforeCommand = command => {
            if (!TestCommandInterceptor.IsClaim(command) || Interlocked.Increment(ref claimCount) != 1)
                return Task.CompletedTask;

            failed.SetResult();
            throw new InvalidOperationException("The database is gone.");
        };

        receiver.BeforeAnswer = async _ => {
            await failed.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken);
        };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.Deliver(CancellationToken));

        // the run ended only when the other message's turn had: its request was answered, and written
        Assert.AreEqual("The database is gone.", exception.Message);
        Assert.AreEqual(1, receiver.Requests.Count);
        await using (var dbContext = host.CreateDbContext()) {
            var rows = await dbContext.Messages.AsNoTracking().OrderBy(x => x.AttemptCount).ToArrayAsync(CancellationToken);
            Assert.AreEqual(WebhookMessageStatus.Pending, rows[0].Status);
            Assert.IsNull(rows[0].LockToken, "the claim that failed holds nothing");
            Assert.AreEqual(WebhookMessageStatus.Succeeded, rows[1].Status);
            Assert.AreEqual(1, rows[1].AttemptCount);
            Assert.AreEqual(1, await dbContext.Attempts.CountAsync(CancellationToken));
        }

        host.Commands.BeforeCommand = null;
        receiver.BeforeAnswer = null;
        var again = await host.Deliver(CancellationToken);
        Assert.AreEqual(1, again.SucceededCount);
        Assert.AreEqual(2, receiver.Requests.Count);
    }
}
