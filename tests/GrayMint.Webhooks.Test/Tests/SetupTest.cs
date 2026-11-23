using GrayMint.Webhooks.Persistence;
using GrayMint.Webhooks.Test.Helper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class SetupTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    // a host's start, which checks the tables
    private static async Task Start(TestWebhookHost host, CancellationToken cancellationToken)
    {
        foreach (var hostedService in host.Services.GetServices<IHostedService>().OfType<WebhooksStartup>())
            await hostedService.StartAsync(cancellationToken);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Setup_makes_the_database_its_tables_and_their_version_any_number_of_times(
        TestDatabaseProvider provider)
    {
        await using var host = TestWebhookHost.CreateEmpty(provider);

        await host.Services.UseGrayMintWebhooks(CancellationToken);
        await host.Services.UseGrayMintWebhooks(CancellationToken);
        await Start(host, CancellationToken);

        await using var dbContext = host.CreateDbContext();
        var metadata = await dbContext.Metadata.AsNoTracking().SingleAsync(CancellationToken);
        Assert.AreEqual(WebhooksSchema.Version, metadata.SchemaVersion);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Setup_puts_the_tables_beside_the_hosts_own_on_its_connection(TestDatabaseProvider provider)
    {
        await using var host = TestWebhookHost.CreateEmpty(provider, onHostConnection: true);
        await host.CreateHostDatabase(CancellationToken);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            shopDbContext.Orders.Add(new TestOrder { State = "new" });
            await shopDbContext.SaveChangesAsync(CancellationToken);
        }

        await host.Services.UseGrayMintWebhooks(CancellationToken);
        await host.Services.UseGrayMintWebhooks(CancellationToken);
        await Start(host, CancellationToken);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var shopDbContext = scope.ServiceProvider.GetRequiredService<TestShopDbContext>();
            Assert.AreEqual(1, await shopDbContext.Orders.CountAsync(CancellationToken), "the host's table is left as it was");
        }
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Start_refuses_a_database_without_the_tables(TestDatabaseProvider provider)
    {
        await using var host = TestWebhookHost.CreateEmpty(provider);
        await using (var dbContext = host.CreateDbContext())
            await dbContext.GetService<IRelationalDatabaseCreator>().CreateAsync(CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Start(host, CancellationToken));

        StringAssert.Contains(exception.Message, "UseGrayMintWebhooks");
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Setup_refuses_a_database_with_only_some_of_the_tables_and_changes_nothing(
        TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        await using (var dbContext = host.CreateDbContext())
            await dbContext.Database.ExecuteSqlRawAsync($"DROP TABLE {WebhooksSchema.Name}.\"{WebhooksSchema.AttemptsTable}\"",
                CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.Services.UseGrayMintWebhooks(CancellationToken));

        StringAssert.Contains(exception.Message, "Only some of the webhooks' tables");
        await using var check = host.CreateDbContext();
        Assert.AreEqual(0, await check.Messages.CountAsync(CancellationToken), "the messages' table is still there");
        var tableCount = await check.Database
            .SqlQueryRaw<int>("SELECT CAST(COUNT(*) AS int) AS \"Value\" FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '" +
                              WebhooksSchema.Name + "'")
            .SingleAsync(CancellationToken);

        Assert.AreEqual(2, tableCount, "the dropped table was not made again");
    }

    [TestMethod]
    public void Context_refuses_a_database_that_is_neither_of_the_two()
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>().UseInMemoryDatabase("webhooks").Options;
        using var dbContext = new WebhooksDbContext(options);

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => dbContext.Model);

        Assert.AreEqual(
            "The webhooks run on PostgreSQL and SQL Server, and their context was given Microsoft.EntityFrameworkCore.InMemory.",
            exception.Message);
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Start_refuses_tables_of_another_version(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken);
        await using (var dbContext = host.CreateDbContext())
            await dbContext.Metadata.ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.SchemaVersion, WebhooksSchema.Version + 1), CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Start(host, CancellationToken));

        StringAssert.Contains(exception.Message, $"are of version {WebhooksSchema.Version + 1}");
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Setup_refuses_a_context_that_retries_on_failure(TestDatabaseProvider provider)
    {
        await using var host = TestWebhookHost.CreateEmpty(provider);
        var services = new ServiceCollection();
        services.AddGrayMintWebhooks();
        services.AddGrayMintWebhooksDb(options => {
            if (provider == TestDatabaseProvider.PostgreSql)
                options.UseNpgsql(host.ConnectionString, x => x.EnableRetryOnFailure());
            else
                options.UseSqlServer(host.ConnectionString, x => x.EnableRetryOnFailure());
        });
        await using var serviceProvider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => serviceProvider.UseGrayMintWebhooks(CancellationToken));

        StringAssert.Contains(exception.Message, "EnableRetryOnFailure");
    }
}
