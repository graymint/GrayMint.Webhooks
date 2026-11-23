using GrayMint.Webhooks.Messages;

namespace GrayMint.Webhooks.Persistence.Models;

// One webhook, queued or done. Only the delivery that holds its lock changes it, but for a retry by hand; and each
// changes it in one statement that checks it may, never by a row it read before.
internal sealed class MessageModel
{
    // the order the messages were queued in: of one subject, the lower goes first
    public long MessageId { get; set; }

    // what the message's id is made of, for the host and the receiver
    public required Guid Uid { get; init; }
    public required string SubscriberId { get; init; }
    public string? Subject { get; init; }
    public required string EventType { get; init; }
    public required string Url { get; init; }
    public required string Payload { get; init; }
    public required DateTime CreatedTime { get; init; }

    // How long the message is worth delivering, as it was queued. The expiry is counted from it again when the
    // message is put back by hand, so it is kept apart from the expiry.
    public long? LifetimeMilliseconds { get; init; }
    public DateTime? ExpirationTime { get; init; }

    public required WebhookMessageStatus Status { get; init; }
    public required DateTime NextAttemptTime { get; init; }
    public int AttemptCount { get; init; }
    public DateTime? LastAttemptTime { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public DateTime? CompletedTime { get; init; }

    // The hold of the one delivery at work on the message, and when another may take it: a host that died with it
    // leaves it to the others.
    public Guid? LockToken { get; init; }
    public DateTime? LockedUntil { get; init; }
}
