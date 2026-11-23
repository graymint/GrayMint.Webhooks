using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GrayMint.Webhooks.Persistence;

// The host's context, whose connection the module's context works on (WebhooksExtension.AddGrayMintWebhooksDb).
internal sealed class HostDatabase(DatabaseFacade database)
{
    public DatabaseFacade Database { get; } = database;
}
