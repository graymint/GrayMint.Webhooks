using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using Microsoft.EntityFrameworkCore;

namespace GrayMint.Webhooks.Test.Helper;

// A host's subscribers that are read from its own tables, as a host on its own connection has them: the lookup is a
// command of the host's context, on the connection the module's commands go by, between a message's claim and the
// write of its outcome.
public sealed class TestShopSubscribers(TestShopDbContext dbContext, TestSubscribers subscribers) : IWebhookSubscribers
{
    public async Task<WebhookSubscriber?> Find(WebhookMessage message, CancellationToken cancellationToken)
    {
        await dbContext.Orders.AnyAsync(cancellationToken);
        return await subscribers.Find(message, cancellationToken);
    }
}
