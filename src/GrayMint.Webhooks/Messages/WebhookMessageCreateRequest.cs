namespace GrayMint.Webhooks.Messages;

/// <summary>A webhook to queue.</summary>
public sealed class WebhookMessageCreateRequest
{
    /// <summary>
    /// The subscriber the message is for, by the host's own id of it: the host is asked for its signing secrets and
    /// headers as the message goes. It doesn't end in white space.
    /// </summary>
    public required string SubscriberId { get; init; }

    /// <summary>The address the message goes to: absolute, http or https, with no user name or password.</summary>
    public required Uri Url { get; init; }

    /// <summary>The kind of event the message tells of, such as <c>order.paid</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The request's body, sent as it is, as JSON.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// What the message is about, such as an order's id, or null. A subscriber's messages of one subject are delivered
    /// in the order they were queued: a later one waits until the earlier one is delivered or given up. That holds
    /// when no two transactions queue for one subject at once, which a host sees to by changing the subject's own
    /// row, such as the order's, before it queues, in the same transaction. It doesn't end in white space.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// How long the message is worth delivering, a millisecond or more; or null for as long as its attempts last.
    /// </summary>
    public TimeSpan? Lifetime { get; init; }
}
