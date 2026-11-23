using GrayMint.Webhooks.Persistence.Models;

namespace GrayMint.Webhooks.Messages;

// What a host sees of the rows.
internal static class WebhookConverter
{
    public static WebhookMessage ToDto(this MessageModel model)
    {
        return new WebhookMessage {
            MessageId = WebhookIds.FormatMessageId(model.Uid),
            SubscriberId = model.SubscriberId,
            Subject = model.Subject,
            EventType = model.EventType,
            Url = new Uri(model.Url),
            Payload = model.Payload,
            Status = model.Status,
            AttemptCount = model.AttemptCount,
            CreatedTime = model.CreatedTime,
            NextAttemptTime = model.Status == WebhookMessageStatus.Pending ? model.NextAttemptTime : null,
            ExpirationTime = model.ExpirationTime,
            LastAttemptTime = model.LastAttemptTime,
            LastStatusCode = model.LastStatusCode,
            LastError = model.LastError,
            CompletedTime = model.CompletedTime
        };
    }

    public static WebhookAttempt ToDto(this AttemptModel model)
    {
        return new WebhookAttempt {
            AttemptTime = model.AttemptTime,
            Duration = TimeSpan.FromMilliseconds(model.DurationMilliseconds),
            IsSuccess = model.IsSuccess,
            StatusCode = model.StatusCode,
            Error = model.Error
        };
    }
}
