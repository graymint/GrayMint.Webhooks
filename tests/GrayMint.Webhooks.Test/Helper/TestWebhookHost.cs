using System.Data.Common;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Helper;

// A host of one test, with a database of its own on one of the two servers compose.yaml starts: the module registered
// as a host registers it, with the test's subscribers and clock. The database has a name nobody else uses and is
// dropped with the test. A second host beside it shares its database, clock and subscribers.
public sealed class TestWebhookHost : IAsyncDisposable
{
    public const string SubscriberId = "shop-1";
    public const string Payload = """{"type":"order.paid","data":{"orderId":"1001"}}""";

    // by address: "localhost" is tried over IPv6 first, where the containers don't listen
    private const string PostgreSqlServer = "Host=127.0.0.1;Port=5442;Username=postgres;Password=postgres";
    private const string SqlServerServer =
        "Server=127.0.0.1,14342;User ID=sa;Password=GrayMint-Webhooks-2026;TrustServerCertificate=true";

    private readonly bool _ownsDatabase;
    private readonly bool _onHostConnection;
    private readonly bool _delivers;
    private readonly Action<WebhookDeliveryOptions>? _configureDelivery;

    public TestDatabaseProvider Provider { get; }
    public string DatabaseName { get; }
    public TestTimeProvider Time { get; }
    public TestSubscribers Subscribers { get; }

    // what the test does to this host's commands, and what this host logged
    public TestCommandInterceptor Commands { get; } = new();
    public TestLog Log { get; } = new();
    public ServiceProvider Services { get; }

    public string ConnectionString => Provider == TestDatabaseProvider.PostgreSql
        ? $"{PostgreSqlServer};Database={DatabaseName}"
        : $"{SqlServerServer};Database={DatabaseName}";

    private TestWebhookHost(TestDatabaseProvider provider, TestWebhookHost? beside, bool onHostConnection, bool delivers,
        Action<WebhookDeliveryOptions>? configureDelivery)
    {
        Provider = provider;
        DatabaseName = beside?.DatabaseName ?? $"test_{Guid.CreateVersion7():N}";
        Time = beside?.Time ?? new TestTimeProvider();
        Subscribers = beside?.Subscribers ?? NewSubscribers();
        _ownsDatabase = beside == null;
        _onHostConnection = onHostConnection;
        _delivers = delivers;
        _configureDelivery = configureDelivery;
        var services = new ServiceCollection();
        Register(services);
        Services = services.BuildServiceProvider();
    }

    public static async Task<TestWebhookHost> Create(TestDatabaseProvider provider, CancellationToken cancellationToken,
        Action<WebhookDeliveryOptions>? configureDelivery = null, bool onHostConnection = false, bool delivers = true)
    {
        var host = new TestWebhookHost(provider, null, onHostConnection, delivers, configureDelivery);
        try {
            if (onHostConnection)
                await host.CreateHostDatabase(cancellationToken);

            await host.Services.UseGrayMintWebhooks(cancellationToken);
            return host;
        }
        catch {
            // no test gets it to drop, so its database goes here
            await host.DisposeAsync();
            throw;
        }
    }

    // only a name: a setup of the test's choice makes the database
    public static TestWebhookHost CreateEmpty(TestDatabaseProvider provider, bool onHostConnection = false,
        bool delivers = true)
    {
        return new TestWebhookHost(provider, null, onHostConnection, delivers, null);
    }

    // a second host of the same app: on the first one's database, with its clock and its subscribers
    public static TestWebhookHost CreateBeside(TestWebhookHost first, bool delivers = true,
        Action<WebhookDeliveryOptions>? configureDelivery = null)
    {
        return new TestWebhookHost(first.Provider, first, first._onHostConnection, delivers, configureDelivery);
    }

    // what a host registers: the module on this database, with the test's subscribers and clock
    public void Register(IServiceCollection services)
    {
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Log));
        services.AddSingleton<TimeProvider>(Time);
        services.AddGrayMintWebhooks();
        if (_onHostConnection) {
            services.AddDbContext<TestShopDbContext>(options => UseDatabase(options));
            services.AddGrayMintWebhooksDb<TestShopDbContext>(
                (connection, options) => UseConnection(options, connection).AddInterceptors(Commands));

            // read from the host's own tables, on the connection the module works on
            services.AddScoped<IWebhookSubscribers>(serviceProvider =>
                new TestShopSubscribers(serviceProvider.GetRequiredService<TestShopDbContext>(), Subscribers));
        }
        else {
            services.AddGrayMintWebhooksDb(options => UseDatabase(options).AddInterceptors(Commands));
            services.AddSingleton<IWebhookSubscribers>(Subscribers);
        }

        if (_delivers)
            services.AddGrayMintWebhookDelivery(options => {
                // the tests' receiver is on this machine, by http; and a test runs the delivery itself
                options.AllowInsecureTargets = true;
                options.PollInterval = TimeSpan.FromHours(1);
                _configureDelivery?.Invoke(options);
            });
    }

    public DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder options)
    {
        return Provider == TestDatabaseProvider.PostgreSql
            ? options.UseNpgsql(ConnectionString)
            : options.UseSqlServer(ConnectionString);
    }

    public DbContextOptionsBuilder UseConnection(DbContextOptionsBuilder options, DbConnection connection)
    {
        return Provider == TestDatabaseProvider.PostgreSql
            ? options.UseNpgsql(connection)
            : options.UseSqlServer(connection);
    }

    // the host's own database and table, which are there before the module's setup
    public async Task CreateHostDatabase(CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TestShopDbContext>().Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<WebhookMessage> Queue(Uri url, CancellationToken cancellationToken, string? subject = null,
        TimeSpan? lifetime = null, string subscriberId = SubscriberId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookMessageService>().Create(
            new WebhookMessageCreateRequest {
                SubscriberId = subscriberId,
                Url = url,
                EventType = "order.paid",
                Payload = Payload,
                Subject = subject,
                Lifetime = lifetime
            }, cancellationToken);
    }

    public async Task<WebhookMessage> Get(string messageId, CancellationToken cancellationToken,
        string subscriberId = SubscriberId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
            .Get(subscriberId, messageId, cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookAttempt>> GetAttempts(string messageId, CancellationToken cancellationToken,
        string subscriberId = SubscriberId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookMessageService>()
            .GetAttempts(subscriberId, messageId, cancellationToken);
    }

    // a run of the delivery, as its job runs it
    public Task<WebhookDeliveryResult> Deliver(CancellationToken cancellationToken)
    {
        return Services.GetRequiredService<WebhookDeliveryService>().DeliverDue(cancellationToken);
    }

    // the module's own context, for a test that looks behind its services
    internal WebhooksDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>();
        UseDatabase(options);
        return new WebhooksDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (!_ownsDatabase)
            return;

        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    private static TestSubscribers NewSubscribers()
    {
        var subscribers = new TestSubscribers();
        subscribers.Set(SubscriberId);
        return subscribers;
    }
}
