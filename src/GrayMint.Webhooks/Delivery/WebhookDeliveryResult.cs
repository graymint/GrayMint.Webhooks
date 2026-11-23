namespace GrayMint.Webhooks.Delivery;

/// <summary>What a run of the delivery did.</summary>
public sealed class WebhookDeliveryResult
{
    /// <summary>The messages their receivers took.</summary>
    public required int SucceededCount { get; init; }

    /// <summary>The attempts that failed, with the messages given up before one.</summary>
    public required int FailedCount { get; init; }
}
