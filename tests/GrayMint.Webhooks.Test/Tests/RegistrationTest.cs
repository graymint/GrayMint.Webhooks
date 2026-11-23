using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class RegistrationTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    [TestMethod]
    [EveryDatabase]
    public async Task Host_that_delivers_does_not_start_without_its_subscribers(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGrayMintWebhooks();
        services.AddGrayMintWebhooksDb(options => host.UseDatabase(options));
        services.AddGrayMintWebhookDelivery();
        await using var serviceProvider = services.BuildServiceProvider();
        var startup = serviceProvider.GetServices<IHostedService>().OfType<WebhooksStartup>().Single();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startup.StartAsync(CancellationToken));

        Assert.AreEqual("A host that delivers webhooks registers its IWebhookSubscribers, and this one has none.", exception.Message);
    }

    [TestMethod]
    public async Task Host_does_not_start_without_a_database_named_for_the_module()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWebhookSubscribers>(new TestSubscribers());
        services.AddGrayMintWebhookDelivery();
        await using var serviceProvider = services.BuildServiceProvider();
        var startup = serviceProvider.GetServices<IHostedService>().OfType<WebhooksStartup>().Single();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startup.StartAsync(CancellationToken));

        Assert.AreEqual("The webhooks have no database. AddGrayMintWebhooksDb names it.", exception.Message);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Delivery_registers_by_itself_what_every_host_of_the_module_has_and_nothing_twice(
        TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWebhookSubscribers>(host.Subscribers);
        services.AddGrayMintWebhookDelivery();
        services.AddGrayMintWebhooks();
        services.AddGrayMintWebhooksDb(options => host.UseDatabase(options));
        await using var serviceProvider = services.BuildServiceProvider();

        // the start's check comes before the job, so a host that can't deliver stops before its first look
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();
        CollectionAssert.AreEqual(new[] { typeof(WebhooksStartup), typeof(WebhookDeliveryJob) },
            hostedServices.Select(x => x.GetType()).ToArray());

        await hostedServices[0].StartAsync(CancellationToken);
        await using var scope = serviceProvider.CreateAsyncScope();
        Assert.AreEqual(1, scope.ServiceProvider.GetServices<WebhookMessageService>().Count());
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Host_that_only_queues_starts_without_subscribers_and_sends_nothing(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        var services = new ServiceCollection();
        services.AddGrayMintWebhooks();
        services.AddGrayMintWebhooksDb(options => host.UseDatabase(options));
        await using var serviceProvider = services.BuildServiceProvider();

        foreach (var hostedService in serviceProvider.GetServices<IHostedService>())
            await hostedService.StartAsync(CancellationToken);

        Assert.IsNull(serviceProvider.GetService<WebhookDeliveryService>());
        await using var scope = serviceProvider.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<WebhookMessageService>().Create(
            new WebhookMessageCreateRequest {
                SubscriberId = TestWebhookHost.SubscriberId, Url = new Uri("https://shop.example.com/webhooks"),
                EventType = "order.paid", Payload = TestWebhookHost.Payload
            }, CancellationToken);

        Assert.AreEqual(WebhookMessageStatus.Pending, message.Status);
    }

    // a host's context that takes the plain DbContextOptions must not get the module's
    [TestMethod]
    public void Hosts_own_context_options_stay_the_hosts()
    {
        using var serviceProvider = new ServiceCollection()
            .AddDbContext<TestShopDbContext>(options => options.UseNpgsql("Host=shop.example.com"))
            .AddGrayMintWebhooks()
            .AddGrayMintWebhooksDb<TestShopDbContext>((connection, options) => options.UseNpgsql(connection))
            .BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions>();

        Assert.AreEqual(typeof(TestShopDbContext), options.ContextType);
    }
}
