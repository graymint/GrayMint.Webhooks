using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrayMint.Webhooks;

// So a host stops at its start, not at its first webhook: with no database named for the module, on a database
// without the tables or with tables of another version, or, when it delivers, without the host's subscribers.
internal sealed class WebhooksStartup(IServiceScopeFactory serviceScopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetService<WebhooksDbContext>() ?? throw new InvalidOperationException(
            "The webhooks have no database. AddGrayMintWebhooksDb names it.");

        if (scope.ServiceProvider.GetService<WebhookDeliveryService>() != null &&
            scope.ServiceProvider.GetService<IWebhookSubscribers>() == null)
            throw new InvalidOperationException(
                $"A host that delivers webhooks registers its {nameof(IWebhookSubscribers)}, and this one has none.");

        await WebhooksSetup.ThrowIfNotReady(dbContext, scope.ServiceProvider.GetService<HostDatabase>(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
