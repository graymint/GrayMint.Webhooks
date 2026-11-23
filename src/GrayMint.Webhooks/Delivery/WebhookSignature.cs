using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GrayMint.Webhooks.Delivery;

// The headers a receiver checks a message by, as the Standard Webhooks specification has them, so its libraries
// verify one: the message's id, the time of the attempt, and a signature of both with the body.
internal static class WebhookSignature
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    private const string SecretPrefix = "whsec_";

    // "v1," and the HMAC-SHA256 of "<id>.<unix seconds>.<body>" in base64, one for each secret, a space between
    public static string Compute(string messageId, long unixSeconds, string payload, IReadOnlyList<string> secrets)
    {
        if (secrets.Count == 0)
            throw new FormatException("The subscriber has no signing secret, and a message never goes unsigned.");

        var signed = Encoding.UTF8.GetBytes(
            $"{messageId}.{unixSeconds.ToString(CultureInfo.InvariantCulture)}.{payload}");

        return string.Join(' ', secrets.Select(x => "v1," + Convert.ToBase64String(HMACSHA256.HashData(GetKey(x), signed))));
    }

    // A secret is base64, with "whsec_" before it or not, which is how the specification's libraries read every
    // secret: one read as plain text here would sign what they refuse.
    private static byte[] GetKey(string secret)
    {
        var text = secret.AsSpan();
        var encoded = text.StartsWith(SecretPrefix) ? text[SecretPrefix.Length..] : text;
        var key = new byte[encoded.Length];
        if (!Convert.TryFromBase64Chars(encoded, key, out var keyLength) || keyLength == 0)
            throw new FormatException("A signing secret of the subscriber is empty, or is not base64.");

        return key[..keyLength];
    }
}
