using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;

namespace GrayMint.Webhooks.Test.Tests;

// A message is one delivery's at a time, whichever host runs it.
[TestClass]
public class ClaimTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    private Task<TestWebhookHost> CreateHost(TestDatabaseProvider provider, bool onHostConnection)
    {
        return TestWebhookHost.Create(provider, CancellationToken, onHostConnection: onHostConnection);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Two_hosts_that_deliver_at_once_send_each_message_once(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        const int messageCount = 40;
        await using var host = await CreateHost(provider, onHostConnection);
        await using var hostBeside = TestWebhookHost.CreateBeside(host);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        for (var i = 0; i < messageCount; i++)
            await host.Queue(receiver.Url($"/webhooks/{i}"), CancellationToken);

        // both find the same messages due, and meet at each one
        var results = await Task.WhenAll(host.Deliver(CancellationToken), hostBeside.Deliver(CancellationToken));

        Assert.AreEqual(messageCount, results.Sum(x => x.SucceededCount));
        Assert.AreEqual(messageCount, receiver.Requests.Count);
        Assert.AreEqual(messageCount, receiver.Requests.Select(x => x.Path).Distinct().Count());
        await using var dbContext = host.CreateDbContext();
        Assert.AreEqual(messageCount, await dbContext.Attempts.CountAsync(CancellationToken));
        Assert.AreEqual(messageCount, await dbContext.Messages.CountAsync(
            x => x.Status == WebhookMessageStatus.Succeeded && x.AttemptCount == 1 && x.LockToken == null, CancellationToken));
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Host_that_found_a_message_due_takes_none_that_another_took_meanwhile(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var hostBeside = TestWebhookHost.CreateBeside(host);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BeforeAnswer = _ => answer.Task;

        // The second host has found the message due, and stops before its claim: the first host takes the message and
        // has its request under way. The claim that follows checks again, and changes no row.
        var firstRuns = new List<Task<WebhookDeliveryResult>>();
        hostBeside.Commands.BeforeCommand = async command => {
            if (!TestCommandInterceptor.IsClaim(command) || firstRuns.Count != 0)
                return;

            firstRuns.Add(host.Deliver(CancellationToken));
            await TestWait.Until(() => receiver.Requests.Count == 1, "the first host's request is under way", CancellationToken);
        };

        var second = await hostBeside.Deliver(CancellationToken);

        Assert.AreEqual(0, second.SucceededCount + second.FailedCount);
        await using (var dbContext = host.CreateDbContext()) {
            // the hold lasts as long as a turn's steps may take together: 10 seconds to the request, its 15, and 20 to write
            var held = await dbContext.Messages.AsNoTracking().SingleAsync(CancellationToken);
            Assert.IsNotNull(held.LockToken);
            Assert.IsNotNull(held.LockedUntil);
            Assert.IsTrue((now + TimeSpan.FromSeconds(45) - held.LockedUntil.Value).Duration() < TimeSpan.FromMilliseconds(1),
                $"Held until {held.LockedUntil:O}.");
        }

        answer.SetResult();
        var first = await firstRuns.Single();
        Assert.AreEqual(1, first.SucceededCount);
        Assert.AreEqual(1, receiver.Requests.Count, "the message went once");
        Assert.AreEqual(1, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Message_a_host_died_with_goes_to_another_once_its_hold_is_over(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var now = host.Time.GetUtcNow().UtcDateTime;
        await using (var dbContext = host.CreateDbContext())
            await dbContext.Messages.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockToken, Guid.CreateVersion7())
                .SetProperty(x => x.LockedUntil, now + TimeSpan.FromSeconds(45)), CancellationToken);

        host.Time.Advance(TimeSpan.FromSeconds(44));
        await host.Deliver(CancellationToken);
        Assert.AreEqual(0, receiver.Requests.Count, "it is still the dead host's");

        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.Deliver(CancellationToken);

        Assert.AreEqual(1, receiver.Requests.Count);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Run_that_is_cancelled_lets_its_message_go_as_it_was(TestDatabaseProvider provider, bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.Delay = TimeSpan.FromSeconds(30);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        // as a host that stops while the receiver has not answered yet
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var run = host.Deliver(stopping.Token);
        await TestWait.Until(() => receiver.Requests.Count == 1, "the request arrived", CancellationToken);
        await stopping.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        await using (var dbContext = host.CreateDbContext()) {
            var row = await dbContext.Messages.AsNoTracking().SingleAsync(CancellationToken);
            Assert.AreEqual(WebhookMessageStatus.Pending, row.Status);
            Assert.AreEqual(0, row.AttemptCount, "an attempt that was let go counts as none");
            Assert.IsNull(row.LockToken, "it is held no more");
            Assert.IsNull(row.LockedUntil);
            Assert.AreEqual(0, await dbContext.Attempts.CountAsync(CancellationToken));
        }

        // so it goes again at once, with no wait for a hold to run out
        receiver.Delay = TimeSpan.Zero;
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Run_that_is_cancelled_during_a_lookup_lets_its_message_go_as_it_was(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        // as a host that stops while it is asked whose the message is
        var lookupBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Subscribers.BeforeAnswer = token => {
            lookupBegan.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        };

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var run = host.Deliver(stopping.Token);
        await lookupBegan.Task.WaitAsync(CancellationToken);
        await stopping.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        Assert.AreEqual(0, receiver.Requests.Count);
        await using (var dbContext = host.CreateDbContext()) {
            var row = await dbContext.Messages.AsNoTracking().SingleAsync(CancellationToken);
            Assert.AreEqual(WebhookMessageStatus.Pending, row.Status);
            Assert.AreEqual(0, row.AttemptCount);
            Assert.IsNull(row.LockToken, "it is held no more");
            Assert.IsNull(row.LastError, "and is not made to wait, as after a lookup that failed");
            Assert.AreEqual(row.CreatedTime, row.NextAttemptTime);
        }

        host.Subscribers.BeforeAnswer = null;
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Succeeded, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    [TestMethod]
    [EveryDatabaseAndConnection]
    public async Task Outcome_is_not_written_by_a_delivery_that_holds_the_message_no_more(TestDatabaseProvider provider,
        bool onHostConnection)
    {
        await using var host = await CreateHost(provider, onHostConnection);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var othersLockToken = Guid.CreateVersion7();

        // while the request is under way, the message becomes another delivery's, as after a hold that ran out
        receiver.BeforeAnswer = async _ => {
            await using var dbContext = host.CreateDbContext();
            await dbContext.Messages.ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LockToken, othersLockToken), CancellationToken);
        };

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(0, result.SucceededCount + result.FailedCount, "what was not written is not counted");
        Assert.AreEqual(1, receiver.Requests.Count);
        await using var check = host.CreateDbContext();
        var row = await check.Messages.AsNoTracking().SingleAsync(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Pending, row.Status);
        Assert.AreEqual(0, row.AttemptCount);
        Assert.AreEqual(othersLockToken, row.LockToken, "the other delivery's hold is as it was");
        Assert.AreEqual(0, await check.Attempts.CountAsync(CancellationToken));
        Assert.AreEqual(message.MessageId, WebhookIds.FormatMessageId(row.Uid));
    }
}
