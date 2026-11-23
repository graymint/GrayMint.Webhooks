using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Tests;

// The delivery's job, as a host runs it.
[TestClass]
public class JobTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    private static IHostedService GetJob(TestWebhookHost host)
    {
        return host.Services.GetServices<IHostedService>().OfType<WebhookDeliveryJob>().Single();
    }

    private static WebhookMessageCreateRequest Request(Uri url)
    {
        return new WebhookMessageCreateRequest {
            SubscriberId = TestWebhookHost.SubscriberId, Url = url, EventType = "order.paid", Payload = TestWebhookHost.Payload
        };
    }

    // The job, started, with its first run over and its signal not set: it waits. What arrives after this arrives
    // by what the test does next, and not by a run that was under way anyhow.
    private async Task<IHostedService> StartJobAndLetItWait(TestWebhookHost host, TestReceiver receiver)
    {
        // queued through another host of the app, which has a signal of its own
        await using var otherHost = TestWebhookHost.CreateBeside(host, delivers: false);
        await otherHost.Queue(receiver.Url("/first-run"), CancellationToken);
        var job = GetJob(host);
        await job.StartAsync(CancellationToken);
        await TestWait.Until(() => receiver.Requests.Count == 1, "the job's first run delivered", CancellationToken);

        // a run waits for the one before it to end: when this one is over, so is the job's first
        await host.Deliver(CancellationToken);
        return job;
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Job_delivers_at_once_a_message_that_its_own_call_committed(TestDatabaseProvider provider)
    {
        // an hour between its looks: only the signal can wake it
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var job = await StartJobAndLetItWait(host, receiver);

        // the scope lives on, so it is the commit that woke the job, and not the scope's end
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
            .Create(Request(receiver.Url("/webhooks")), CancellationToken);

        await TestWait.Until(() => receiver.Requests.Count == 2, "the message arrived", CancellationToken);
        await job.StopAsync(CancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Job_delivers_a_message_of_the_hosts_transaction_as_its_scope_ends(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, onHostConnection: true);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var job = await StartJobAndLetItWait(host, receiver);

        // A job woken as the message is written would look before the host commits, find nothing, and wait its hour.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            await using var transaction = await shopDbContext.Database.BeginTransactionAsync(CancellationToken);
            await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
                .Create(Request(receiver.Url("/webhooks")), CancellationToken);

            await transaction.CommitAsync(CancellationToken);
        }

        await TestWait.Until(() => receiver.Requests.Count == 2, "the message arrived", CancellationToken);
        await job.StopAsync(CancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Job_delivers_at_once_a_message_that_is_put_back(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken, options => options.RetryDelays = []);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        receiver.SetStatusCode("/webhooks", 500);
        var job = await StartJobAndLetItWait(host, receiver);
        var message = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        await TestWait.Until(() => receiver.Requests.Count == 2, "the message's one attempt was made", CancellationToken);

        // the run that made the attempt is over, and the job waits again
        await host.Deliver(CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, (await host.Get(message.MessageId, CancellationToken)).Status);
        receiver.SetStatusCode("/webhooks", 200);

        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
            .Retry(TestWebhookHost.SubscriberId, message.MessageId, CancellationToken);

        await TestWait.Until(() => receiver.Requests.Count == 3, "the message went again", CancellationToken);
        await job.StopAsync(CancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Job_delivers_what_another_host_queued_at_its_next_look(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken,
            options => options.PollInterval = TimeSpan.FromMilliseconds(200));

        await using var queuingHost = TestWebhookHost.CreateBeside(host, delivers: false);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var job = await StartJobAndLetItWait(host, receiver);

        // nothing wakes this host's job for it
        await queuingHost.Queue(receiver.Url("/webhooks"), CancellationToken);

        await TestWait.Until(() => receiver.Requests.Count == 2, "the message arrived", CancellationToken);
        await job.StopAsync(CancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Job_goes_on_after_a_run_that_failed(TestDatabaseProvider provider)
    {
        // its first run fails, as any run does here: the database is not there yet
        await using var host = TestWebhookHost.CreateEmpty(provider);
        await using var receiver = await TestReceiver.Start(CancellationToken);
        var job = GetJob(host);
        await job.StartAsync(CancellationToken);
        await TestWait.Until(
            () => host.Log.Entries.Any(x => x.Level == LogLevel.Error &&
                                            x.Message == "The webhooks' delivery failed; it runs again after its interval."),
            "the job's first run failed", CancellationToken);

        // an hour between its looks: a job that had stopped would not be woken by the message
        await host.Services.UseGrayMintWebhooks(CancellationToken);
        await host.Queue(receiver.Url("/webhooks"), CancellationToken);

        await TestWait.Until(() => receiver.Requests.Count == 1, "the message arrived", CancellationToken);
        await job.StopAsync(CancellationToken);
    }
}
