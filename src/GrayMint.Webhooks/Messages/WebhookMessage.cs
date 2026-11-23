namespace GrayMint.Webhooks.Messages;

/// <summary>A webhook: what it carries, whom it is for, and where it stands.</summary>
public sealed class WebhookMessage
{
    /// <summary>The message's id, which its receiver gets as <c>webhook-id</c> with every attempt.</summary>
    public required string MessageId { get; init; }

    /// <summary>The subscriber the message is for.</summary>
    public required string SubscriberId { get; init; }

    /// <summary>What the message is about, or null. A subscriber's messages of one subject are delivered in their order.</summary>
    public required string? Subject { get; init; }

    /// <summary>The kind of event the message tells of, such as <c>order.paid</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The address the message goes to.</summary>
    public required Uri Url { get; init; }

    /// <summary>The request's body, as it is sent.</summary>
    public required string Payload { get; init; }

    /// <summary>Where the message stands.</summary>
    public required WebhookMessageStatus Status { get; init; }

    /// <summary>The attempts made since the message was queued, or since it was last put back by hand.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>When the message was queued.</summary>
    public required DateTime CreatedTime { get; init; }

    /// <summary>When the next attempt is due, or null for a message that is done.</summary>
    public required DateTime? NextAttemptTime { get; init; }

    /// <summary>When the message is given up if still undelivered, or null for one that only its attempts end.</summary>
    public required DateTime? ExpirationTime { get; init; }

    /// <summary>When the last attempt was made, or null before the first.</summary>
    public required DateTime? LastAttemptTime { get; init; }

    /// <summary>The status code of the receiver's last answer, or null when it gave none.</summary>
    public required int? LastStatusCode { get; init; }

    /// <summary>Why the last attempt failed or the message was given up, or null.</summary>
    public required string? LastError { get; init; }

    /// <summary>When the message was delivered or given up, or null while it is pending.</summary>
    public required DateTime? CompletedTime { get; init; }
}
