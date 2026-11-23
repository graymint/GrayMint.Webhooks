using System.Linq.Expressions;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Persistence;
using GrayMint.Webhooks.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Delivery;

// One message's turn: it is claimed for this delivery alone, its subscriber is looked up, it is sent, and what came
// of it is written. A claim's hold lasts as long as the three steps after it may take together (the way to the
// request, the request, the write), so a delivery is done with a message before another may take it.
internal sealed class WebhookTurn
{
    // how long a message waits when the host could not get it ready, such as on a database that is down for a while
    private static readonly TimeSpan NotReadyDelay = TimeSpan.FromMinutes(1);

    // An outcome is written within this, even while the host stops: a message that was sent and not written would be
    // sent again.
    private static readonly TimeSpan OutcomeSaveTimeout = TimeSpan.FromSeconds(20);

    // What a message that waits says of it. The host's own failure stays in the log: a host may show a message's
    // error to its subscriber.
    private const string LookupFailedError = "The host could not look up the message's subscriber; the message waits.";
    private const string LateError = "The host took too long to get the message ready; the message waits.";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly WebhookSender _sender;
    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookTurn> _logger;

    public WebhookTurn(IServiceScopeFactory serviceScopeFactory, WebhookSender sender, WebhookDeliveryOptions options,
        TimeProvider timeProvider, ILogger<WebhookTurn> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _sender = sender;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // A message that may go now: pending, due, held by no delivery, and with no older pending message of its
    // subscriber and subject, which goes first. An older one that fails keeps the later ones waiting until it is
    // delivered or given up.
    public static Expression<Func<MessageModel, bool>> IsDue(WebhooksDbContext dbContext, DateTime now)
    {
        return x => x.Status == WebhookMessageStatus.Pending && x.NextAttemptTime <= now &&
                    (x.LockedUntil == null || x.LockedUntil <= now) &&
                    (x.Subject == null || !dbContext.Messages.Any(older =>
                        older.Status == WebhookMessageStatus.Pending && older.SubscriberId == x.SubscriberId &&
                        older.Subject == x.Subject && older.MessageId < x.MessageId));
    }

    // null: the message was not this delivery's to take, or was its own no more when its outcome came
    public async Task<WebhookSendKind?> Run(long messageId, CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
        var hostDatabase = scope.ServiceProvider.GetService<HostDatabase>();

        // taken before the claim reads the clock: the hold is counted from there, and so is the way to the request
        var startTimestamp = _timeProvider.GetTimestamp();
        var lockToken = Guid.CreateVersion7();
        var message = await Claim(dbContext, hostDatabase, messageId, lockToken, cancellationToken);
        if (message == null)
            return null;

        var result = await Send(scope.ServiceProvider, message, startTimestamp, cancellationToken);
        var isWritten = await SaveOutcome(dbContext, hostDatabase, message, lockToken, result);
        return isWritten ? result.Kind : null;
    }

    // Takes the message for this delivery alone, in one statement that checks again that it may go: of two hosts that
    // found it due, one changes the row and the other changes none.
    private async Task<MessageModel?> Claim(WebhooksDbContext dbContext, HostDatabase? hostDatabase, long messageId,
        Guid lockToken, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var lockedUntil = now + _options.StartTimeout + _options.AttemptTimeout + OutcomeSaveTimeout;

        await using var session = await WebhookSession.Begin(dbContext, hostDatabase, isWrite: false, cancellationToken);
        var claimed = await dbContext.Messages
            .Where(x => x.MessageId == messageId)
            .Where(IsDue(dbContext, now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockToken, lockToken)
                .SetProperty(x => x.LockedUntil, lockedUntil), cancellationToken);

        if (claimed == 0)
            return null;

        // read by the hold too: after a wait longer than the hold, another delivery may have it
        return await dbContext.Messages
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.MessageId == messageId && x.LockToken == lockToken, cancellationToken);
    }

    private async Task<WebhookSendResult> Send(IServiceProvider scopeServices, MessageModel message, long startTimestamp,
        CancellationToken cancellationToken)
    {
        if (message.ExpirationTime <= _timeProvider.GetUtcNow().UtcDateTime)
            return new WebhookSendResult {
                Kind = WebhookSendKind.GivenUp, Error = "The message expired before it was delivered."
            };

        // a lookup that hangs, as on a database that is gone, ends here and frees its slot
        using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupTimeout.CancelAfter(_options.StartTimeout);
        try {
            var subscriber = await scopeServices.GetRequiredService<IWebhookSubscribers>()
                .Find(message.ToDto(), lookupTimeout.Token);

            if (subscriber == null)
                return new WebhookSendResult { Kind = WebhookSendKind.GivenUp, Error = "The subscriber is no more." };

            // The hold leaves this long to get to the request. After a claim that waited on the database, or a lookup
            // that ran on past its token, a request could still be under way when another delivery takes the message.
            var startDuration = _timeProvider.GetElapsedTime(startTimestamp);
            if (startDuration <= _options.StartTimeout)
                return await _sender.Send(message, subscriber, cancellationToken);

            _logger.LogError(
                "A webhook was ready to go only after {StartDuration} seconds, and it has {StartTimeout}; it waits. MessageId: {MessageId}",
                startDuration.TotalSeconds, _options.StartTimeout.TotalSeconds, WebhookIds.FormatMessageId(message.Uid));

            return new WebhookSendResult { Kind = WebhookSendKind.Postponed, Error = LateError };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return new WebhookSendResult { Kind = WebhookSendKind.Canceled };
        }
        catch (Exception ex) {
            // the host's own failure, not the receiver's: it costs the message no attempt
            _logger.LogError(ex, "Could not look up the subscriber of a webhook; it waits. MessageId: {MessageId}",
                WebhookIds.FormatMessageId(message.Uid));

            return new WebhookSendResult { Kind = WebhookSendKind.Postponed, Error = LookupFailedError };
        }
    }

    // The message and its attempt are written together, so the queue and the history always agree; and only while
    // this delivery still holds the message, or another's outcome would be written over.
    // false: it held the message no more, and nothing was written.
    private async Task<bool> SaveOutcome(WebhooksDbContext dbContext, HostDatabase? hostDatabase, MessageModel message,
        Guid lockToken, WebhookSendResult result)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var attemptCount = message.AttemptCount + (result.IsAttempt ? 1 : 0);
        var retryTime = result.Kind == WebhookSendKind.Failed ? FindRetryTime(message, attemptCount, now) : null;
        var status = result.Kind switch {
            WebhookSendKind.Succeeded => WebhookMessageStatus.Succeeded,
            WebhookSendKind.Failed when retryTime != null => WebhookMessageStatus.Pending,
            WebhookSendKind.Failed or WebhookSendKind.FailedForGood or WebhookSendKind.GivenUp => WebhookMessageStatus.Failed,
            WebhookSendKind.Postponed or WebhookSendKind.Canceled => WebhookMessageStatus.Pending,
            _ => throw new InvalidOperationException($"Unknown kind of a webhook's outcome: {result.Kind}.")
        };

        var nextAttemptTime = result.Kind == WebhookSendKind.Postponed ? now + NotReadyDelay : retryTime ?? message.NextAttemptTime;
        var completedTime = status == WebhookMessageStatus.Pending ? (DateTime?)null : now;
        var lastAttemptTime = result.IsAttempt ? result.AttemptTime : message.LastAttemptTime;
        var lastStatusCode = result.IsAttempt ? result.StatusCode : message.LastStatusCode;

        // a turn that was let go says nothing new of the message
        var lastError = result.Kind == WebhookSendKind.Canceled ? message.LastError : Truncate(result.Error);

        // not the caller's token: a stopping host still writes what it has sent
        using var timeout = new CancellationTokenSource(OutcomeSaveTimeout);
        await using var session = await WebhookSession.Begin(dbContext, hostDatabase, isWrite: true, timeout.Token);

        // The message first, in a statement that checks the hold: a hold that ran out, or a message that was removed
        // meanwhile, changes no row, and then no attempt is written either. Written the other way round, the attempt
        // of a removed message would fail the whole run, and would meet a removal in the opposite order of rows.
        var written = await dbContext.Messages
            .Where(x => x.MessageId == message.MessageId && x.LockToken == lockToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockToken, (Guid?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null)
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.NextAttemptTime, nextAttemptTime)
                .SetProperty(x => x.AttemptCount, attemptCount)
                .SetProperty(x => x.LastAttemptTime, lastAttemptTime)
                .SetProperty(x => x.LastStatusCode, lastStatusCode)
                .SetProperty(x => x.LastError, lastError)
                .SetProperty(x => x.CompletedTime, completedTime), timeout.Token);

        if (written == 0) {
            _logger.LogWarning("A webhook's outcome was not written, as its delivery held it no more. MessageId: {MessageId}",
                WebhookIds.FormatMessageId(message.Uid));

            return false;
        }

        if (result.IsAttempt) {
            dbContext.Attempts.Add(new AttemptModel {
                MessageId = message.MessageId,
                AttemptTime = result.AttemptTime,
                DurationMilliseconds = (int)Math.Min(result.Duration.TotalMilliseconds, int.MaxValue),
                IsSuccess = result.Kind == WebhookSendKind.Succeeded,
                StatusCode = result.StatusCode,
                Error = Truncate(result.Error)
            });

            await dbContext.SaveChangesAsync(timeout.Token);
        }

        await session.Commit(timeout.Token);
        LogOutcome(message, result, attemptCount, status);
        return true;
    }

    // When a message whose attempt failed goes again: after the wait of that attempt's number. Null past the last
    // wait, or when the wait would end at the message's expiry or after it: the message is given up.
    private DateTime? FindRetryTime(MessageModel message, int attemptCount, DateTime now)
    {
        if (attemptCount > _options.RetryDelays.Count)
            return null;

        var retryTime = now + _options.RetryDelays[attemptCount - 1];
        return retryTime >= message.ExpirationTime ? null : retryTime;
    }

    private void LogOutcome(MessageModel message, WebhookSendResult result, int attemptCount, WebhookMessageStatus status)
    {
        var messageId = WebhookIds.FormatMessageId(message.Uid);
        switch (result.Kind) {
            case WebhookSendKind.Succeeded:
                _logger.LogDebug("A webhook was delivered. MessageId: {MessageId}", messageId);
                break;

            case WebhookSendKind.Failed:
                _logger.LogWarning(
                    "A webhook's attempt failed. MessageId: {MessageId}, Attempt: {AttemptCount}, GivenUp: {IsGivenUp}, Error: {Error}",
                    messageId, attemptCount, status == WebhookMessageStatus.Failed, result.Error);

                break;

            case WebhookSendKind.FailedForGood:
            case WebhookSendKind.GivenUp:
                _logger.LogWarning("A webhook was given up. MessageId: {MessageId}, Error: {Error}", messageId, result.Error);
                break;
        }
    }

    private static string? Truncate(string? error)
    {
        return error?.Length > WebhooksDbContext.MaxErrorLength ? error[..WebhooksDbContext.MaxErrorLength] : error;
    }
}
