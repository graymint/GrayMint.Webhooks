using System.Collections.ObjectModel;

namespace GrayMint.Webhooks.Delivery;

/// <summary>What a subscriber's message is sent with.</summary>
public sealed class WebhookSubscriber
{
    /// <summary>
    /// The secrets a message is signed with, the current one first: at least one, as a message never goes unsigned.
    /// Each gives a signature, so a receiver that still holds the old secret keeps verifying while it changes. A
    /// secret is base64, with <c>whsec_</c> before it or not, as the Standard Webhooks specification has it and its
    /// libraries read it; a host whose secret is plain text gives the base64 of its bytes.
    /// </summary>
    public required IReadOnlyList<string> SigningSecrets { get; init; }

    /// <summary>
    /// Headers sent with the message, such as an <c>Authorization</c> the subscriber asked for. The module's own
    /// headers can't be among them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
