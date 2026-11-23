namespace GrayMint.Webhooks.Delivery;

// What a claimed message's turn came to, for the delivery to write.
internal sealed class WebhookSendResult
{
    public required WebhookSendKind Kind { get; init; }

    // an attempt's own: when it began, how long it took and what the receiver answered
    public DateTime AttemptTime { get; init; }
    public TimeSpan Duration { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }

    public bool IsAttempt => Kind is WebhookSendKind.Succeeded or WebhookSendKind.Failed or WebhookSendKind.FailedForGood;
}
