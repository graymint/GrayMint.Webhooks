namespace GrayMint.Webhooks;

/// <summary>The most a message may hold, and the most a call hands back.</summary>
public static class WebhookLimits
{
    /// <summary>The most characters of a subscriber's id.</summary>
    public const int MaxSubscriberIdLength = 100;

    /// <summary>The most characters of a message's subject.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>The most characters of a message's event type.</summary>
    public const int MaxEventTypeLength = 100;

    /// <summary>The most characters of the address a message goes to.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The most characters of a message's payload.</summary>
    public const int MaxPayloadLength = 256 * 1024;

    /// <summary>The most messages a page has.</summary>
    public const int MaxPageSize = 100;
}
