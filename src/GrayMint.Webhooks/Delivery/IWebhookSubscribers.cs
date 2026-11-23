using GrayMint.Webhooks.Messages;

namespace GrayMint.Webhooks.Delivery;

/// <summary>
/// The host's subscribers: what each one's messages are sent with. A host that delivers registers its own, for a
/// scope or as a singleton.
/// </summary>
public interface IWebhookSubscribers
{
    /// <summary>
    /// Finds the subscriber of a message that is about to go. It is asked before every attempt, so a changed secret
    /// or header counts from the next one. It is quick: the message is held for its delivery meanwhile. When it
    /// throws, or has not answered after ten seconds, the message waits a minute and loses no attempt; what it threw
    /// goes to the log, never into the message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The token that cancels the call: the host is stopping, or the ten seconds are over.</param>
    /// <returns>The subscriber, or null when it is no more, which gives the message up.</returns>
    Task<WebhookSubscriber?> Find(WebhookMessage message, CancellationToken cancellationToken);
}
