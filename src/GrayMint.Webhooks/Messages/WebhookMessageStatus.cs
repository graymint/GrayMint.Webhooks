namespace GrayMint.Webhooks.Messages;

/// <summary>Where a message stands.</summary>
public enum WebhookMessageStatus
{
    /// <summary>It waits for its first attempt, or for the next one after a failure.</summary>
    Pending,

    /// <summary>Its receiver took it.</summary>
    Succeeded,

    /// <summary>It is given up: its attempts are used up, it expired, or it can never be delivered.</summary>
    Failed
}
