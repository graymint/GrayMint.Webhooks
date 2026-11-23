using System.Collections.Concurrent;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;

namespace GrayMint.Webhooks.Test.Helper;

// The host's subscribers, as a test sets them: a subscriber it has not set is no more.
public sealed class TestSubscribers : IWebhookSubscribers
{
    // base64, as the specification has a secret
    public const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private readonly ConcurrentDictionary<string, WebhookSubscriber> _subscribers = new();

    // what Find fails with, as a host whose own database is down
    public Exception? Failure { get; set; }

    // runs before Find answers, with the token Find was given: a lookup that is slow, or one that never ends
    public Func<CancellationToken, Task>? BeforeAnswer { get; set; }

    public void Set(string subscriberId)
    {
        Set(subscriberId, new WebhookSubscriber { SigningSecrets = [Secret] });
    }

    public void Set(string subscriberId, WebhookSubscriber subscriber)
    {
        _subscribers[subscriberId] = subscriber;
    }

    public void Remove(string subscriberId)
    {
        _subscribers.TryRemove(subscriberId, out _);
    }

    public async Task<WebhookSubscriber?> Find(WebhookMessage message, CancellationToken cancellationToken)
    {
        if (BeforeAnswer != null)
            await BeforeAnswer(cancellationToken);

        return Failure != null ? throw Failure : _subscribers.GetValueOrDefault(message.SubscriberId);
    }
}
