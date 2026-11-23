namespace GrayMint.Webhooks.Messages;

/// <summary>One try to deliver a message, and how it ended.</summary>
public sealed class WebhookAttempt
{
    /// <summary>When the attempt began.</summary>
    public required DateTime AttemptTime { get; init; }

    /// <summary>How long the attempt took.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Whether the receiver took the message.</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>The status code of the receiver's answer, or null when it gave none.</summary>
    public required int? StatusCode { get; init; }

    /// <summary>Why the attempt failed, or null for one that succeeded.</summary>
    public required string? Error { get; init; }
}
