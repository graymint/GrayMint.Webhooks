using GrayMint.Common.Exceptions;

namespace GrayMint.Webhooks;

// A message's id goes out as text, so its key's type inside can change without the API changing. It is the one its
// receiver gets too (webhook-id): made from a UUID, it tells nothing of how many messages there are, and is not
// another database's. An id of another form names nothing, so it is refused as one that is not there. A row's own
// key never goes out: it counts every subscriber's rows.
internal static class WebhookIds
{
    private const string MessagePrefix = "msg_";

    public static string FormatMessageId(Guid uid)
    {
        return MessagePrefix + uid.ToString("N");
    }

    public static Guid ParseMessageId(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        return messageId.StartsWith(MessagePrefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(messageId.AsSpan(MessagePrefix.Length), "N", out var uid)
            ? uid
            : throw new NotExistsException($"Could not find the message. Id: {messageId}");
    }
}
