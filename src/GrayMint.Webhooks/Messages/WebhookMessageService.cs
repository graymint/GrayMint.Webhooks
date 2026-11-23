using System.Diagnostics.CodeAnalysis;
using GrayMint.Common.Exceptions;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Persistence;
using GrayMint.Webhooks.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMint.Webhooks.Messages;

/// <summary>
/// The host's hand on its webhooks: it queues a message, reads how messages and their attempts stand, puts a failed
/// message back, and removes a subscriber's. A message is its subscriber's alone: every call names the subscriber, and
/// one's message is not there for another.
/// </summary>
public sealed class WebhookMessageService : IDisposable
{
    private readonly WebhooksDbContext _dbContext;
    private readonly HostDatabase? _hostDatabase;
    private readonly WebhookDeliverySignal _deliverySignal;
    private readonly TimeProvider _timeProvider;
    private bool _isSignalDueAtDispose;

    internal WebhookMessageService(WebhooksDbContext dbContext, HostDatabase? hostDatabase,
        WebhookDeliverySignal deliverySignal, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _hostDatabase = hostDatabase;
        _deliverySignal = deliverySignal;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Queues a message. On the host's connection it is written in the transaction the host has open, so it commits
    /// with the change it tells of, or goes with it. A host that delivers makes the first attempt at once when this
    /// call committed the message, and as this scope ends when the message is in a transaction of the host's; any
    /// other host that delivers finds it at its next look.
    /// </summary>
    /// <param name="request">The message.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The message, pending.</returns>
    /// <exception cref="ArgumentException">A value of the request is not as <see cref="WebhookMessageCreateRequest"/> says.</exception>
    public async Task<WebhookMessage> Create(WebhookMessageCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var now = _timeProvider.GetUtcNow();
        var lifetimeMilliseconds = (long?)request.Lifetime?.TotalMilliseconds;
        var message = new MessageModel {
            Uid = Guid.CreateVersion7(now),
            SubscriberId = request.SubscriberId,
            Subject = request.Subject,
            EventType = request.EventType,
            Url = request.Url.AbsoluteUri,
            Payload = request.Payload,
            CreatedTime = now.UtcDateTime,
            LifetimeMilliseconds = lifetimeMilliseconds,
            ExpirationTime = GetExpirationTime(now.UtcDateTime, lifetimeMilliseconds),
            Status = WebhookMessageStatus.Pending,
            NextAttemptTime = now.UtcDateTime
        };

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: true, cancellationToken);
        try {
            _dbContext.Messages.Add(message);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally {
            // nothing of a call stays in the context for the next one, a row that failed to save least of all
            _dbContext.ChangeTracker.Clear();
        }

        await session.Commit(cancellationToken);
        SignalDelivery(session);
        return message.ToDto();
    }

    /// <summary>Gets a message of the subscriber.</summary>
    /// <param name="subscriberId">The subscriber.</param>
    /// <param name="messageId">The message's id.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The message.</returns>
    /// <exception cref="NotExistsException">The subscriber has no such message.</exception>
    public async Task<WebhookMessage> Get(string subscriberId, string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriberId);
        var uid = WebhookIds.ParseMessageId(messageId);

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: false, cancellationToken);
        var message = await GetMessage(subscriberId, uid, messageId, cancellationToken);
        return message.ToDto();
    }

    /// <summary>Gets a subscriber's messages, the newest first, a page at a time.</summary>
    /// <param name="subscriberId">The subscriber.</param>
    /// <param name="subject">The subject the messages are about, or null for those of any.</param>
    /// <param name="beforeMessageId">Null for the first page; for the next one, the id of the last message of the page before.</param>
    /// <param name="limit">The most messages a page has: 1 to <see cref="WebhookLimits.MaxPageSize"/>.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The messages.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not 1 to <see cref="WebhookLimits.MaxPageSize"/>.</exception>
    /// <exception cref="NotExistsException">The subscriber has no message of <paramref name="beforeMessageId"/>.</exception>
    public async Task<IReadOnlyList<WebhookMessage>> GetBySubscriber(string subscriberId, string? subject,
        string? beforeMessageId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriberId);
        if (limit is < 1 or > WebhookLimits.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                $"A page has 1 to {WebhookLimits.MaxPageSize} messages.");

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: false, cancellationToken);
        var beforeDbId = beforeMessageId == null
            ? (long?)null
            : (await GetMessage(subscriberId, WebhookIds.ParseMessageId(beforeMessageId), beforeMessageId, cancellationToken)).MessageId;

        if (!IsKey(subscriberId) || (subject != null && !IsKey(subject)))
            return [];

        var messages = await _dbContext.Messages
            .AsNoTracking()
            .Where(x => x.SubscriberId == subscriberId)
            .Where(x => subject == null || x.Subject == subject)
            .Where(x => beforeDbId == null || x.MessageId < beforeDbId)
            .OrderByDescending(x => x.MessageId)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

        return messages.Select(x => x.ToDto()).ToArray();
    }

    /// <summary>Gets the attempts made to deliver a message, the first one first.</summary>
    /// <param name="subscriberId">The subscriber.</param>
    /// <param name="messageId">The message's id.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The attempts.</returns>
    /// <exception cref="NotExistsException">The subscriber has no such message.</exception>
    public async Task<IReadOnlyList<WebhookAttempt>> GetAttempts(string subscriberId, string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriberId);
        var uid = WebhookIds.ParseMessageId(messageId);

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: false, cancellationToken);
        var message = await GetMessage(subscriberId, uid, messageId, cancellationToken);
        var attempts = await _dbContext.Attempts
            .AsNoTracking()
            .Where(x => x.MessageId == message.MessageId)
            .OrderBy(x => x.AttemptId)
            .ToArrayAsync(cancellationToken);

        return attempts.Select(x => x.ToDto()).ToArray();
    }

    /// <summary>
    /// Puts a failed message back: it is pending again, due now, with all its attempts ahead of it and as long to
    /// live as it first had. The attempts made so far stay in its history. It goes after the messages of its subject
    /// that were delivered meanwhile: a message put back by hand is out of its subject's order.
    /// </summary>
    /// <param name="subscriberId">The subscriber.</param>
    /// <param name="messageId">The message's id.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The message, pending.</returns>
    /// <exception cref="NotExistsException">The subscriber has no such message.</exception>
    /// <exception cref="InvalidOperationException">The message has not failed: it is pending, or was delivered.</exception>
    public async Task<WebhookMessage> Retry(string subscriberId, string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriberId);
        var uid = WebhookIds.ParseMessageId(messageId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: true, cancellationToken);
        var message = await GetMessage(subscriberId, uid, messageId, cancellationToken);
        var expirationTime = GetExpirationTime(now, message.LifetimeMilliseconds);

        // only a failed one: a delivery may hold a pending one, and a delivered one must not go twice
        var updated = await _dbContext.Messages
            .Where(x => x.MessageId == message.MessageId && x.Status == WebhookMessageStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, WebhookMessageStatus.Pending)
                .SetProperty(x => x.AttemptCount, 0)
                .SetProperty(x => x.NextAttemptTime, now)
                .SetProperty(x => x.ExpirationTime, expirationTime)
                .SetProperty(x => x.CompletedTime, (DateTime?)null), cancellationToken);

        if (updated == 0)
            throw new InvalidOperationException(
                $"Only a failed message is put back, and message {messageId} has not failed.");

        var retried = await GetMessage(subscriberId, uid, messageId, cancellationToken);
        await session.Commit(cancellationToken);
        SignalDelivery(session);
        return retried.ToDto();
    }

    /// <summary>
    /// Removes every message of a subscriber, with its attempts, as when the subscriber itself is removed. A message
    /// whose request is under way is removed too: that request is not called back, and nothing is kept of it.
    /// </summary>
    /// <param name="subscriberId">The subscriber.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The number of messages removed.</returns>
    public async Task<int> DeleteBySubscriber(string subscriberId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriberId);
        if (!IsKey(subscriberId))
            return 0;

        await using var session = await WebhookSession.Begin(_dbContext, _hostDatabase, isWrite: true, cancellationToken);
        var deleted = await _dbContext.Messages
            .Where(x => x.SubscriberId == subscriberId)
            .ExecuteDeleteAsync(cancellationToken);

        await session.Commit(cancellationToken);
        return deleted;
    }

    /// <summary>
    /// Wakes the delivery when the scope queued a message, or put one back, in a transaction of the host's: the
    /// scope's end is the nearest the module sees to that transaction's.
    /// </summary>
    public void Dispose()
    {
        if (_isSignalDueAtDispose)
            _deliverySignal.Set();
    }

    // A message that is committed can go at once. One in a transaction of the host's is not there for a delivery
    // until the host commits, and only the host knows when: a delivery woken now would find nothing.
    private void SignalDelivery(WebhookSession session)
    {
        if (session.IsOwnTransaction)
            _deliverySignal.Set();
        else
            _isSignalDueAtDispose = true;
    }

    private async Task<MessageModel> GetMessage(string subscriberId, Guid uid, string messageId,
        CancellationToken cancellationToken)
    {
        var message = IsKey(subscriberId)
            ? await _dbContext.Messages
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.SubscriberId == subscriberId && x.Uid == uid, cancellationToken)
            : null;

        return message ?? throw new NotExistsException(
            $"Message {messageId} is not the subscriber's. SubscriberId: {subscriberId}");
    }

    private static DateTime? GetExpirationTime(DateTime now, long? lifetimeMilliseconds)
    {
        return lifetimeMilliseconds == null ? null : now + TimeSpan.FromMilliseconds(lifetimeMilliseconds.Value);
    }

    // What names a subscriber or a subject doesn't end in white space. SQL Server compares a text as if the spaces
    // at its end were not there, so "shop" and "shop " would be one subscriber there, and two on PostgreSQL: such a
    // text is refused as a message is queued, and names nothing where one is looked for.
    private static bool IsKey(string value)
    {
        return value.Length != 0 && !char.IsWhiteSpace(value[^1]);
    }

    private static void Validate(WebhookMessageCreateRequest request)
    {
        ThrowIfNotKey(request.SubscriberId, WebhookLimits.MaxSubscriberIdLength, "A subscriber's id");
        ThrowIfNotText(request.EventType, WebhookLimits.MaxEventTypeLength, "An event type");
        ThrowIfNotText(request.Payload, WebhookLimits.MaxPayloadLength, "A payload");
        if (request.Subject != null)
            ThrowIfNotKey(request.Subject, WebhookLimits.MaxSubjectLength, "A subject");

        ArgumentNullException.ThrowIfNull(request.Url, nameof(request));
        if (!request.Url.IsAbsoluteUri || (request.Url.Scheme != Uri.UriSchemeHttps && request.Url.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("A webhook goes to an absolute http or https address.", nameof(request));

        // a password in an address would be kept with the message, and shown with it
        if (request.Url.UserInfo.Length != 0)
            throw new ArgumentException("A webhook's address carries no user name or password.", nameof(request));

        if (request.Url.AbsoluteUri.Length > WebhookLimits.MaxUrlLength)
            throw new ArgumentException($"A webhook's address has up to {WebhookLimits.MaxUrlLength} characters.",
                nameof(request));

        // it is kept in whole milliseconds
        if (request.Lifetime < TimeSpan.FromMilliseconds(1))
            throw new ArgumentException("A message's lifetime is a millisecond or more, or null.", nameof(request));
    }

    private static void ThrowIfNotKey([NotNull] string? value, int maxLength, string name)
    {
        ThrowIfNotText(value, maxLength, name);
        if (!IsKey(value))
            throw new ArgumentException($"{name} does not end in white space.", "request");
    }

    private static void ThrowIfNotText([NotNull] string? value, int maxLength, string name)
    {
        if (value == null || value.Length == 0 || value.Length > maxLength)
            throw new ArgumentException($"{name} has 1 to {maxLength} characters.", "request");

        // PostgreSQL refuses it in a text, in the middle of the write, and SQL Server keeps it
        if (value.Contains('\0'))
            throw new ArgumentException($"{name} has no NUL character.", "request");
    }
}
