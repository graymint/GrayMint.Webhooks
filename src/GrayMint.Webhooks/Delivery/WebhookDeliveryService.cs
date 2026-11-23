using GrayMint.Webhooks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMint.Webhooks.Delivery;

/// <summary>
/// Delivers the messages that are due. The host's job runs it, and a host may run it itself. A message is claimed in
/// the database before it is sent, so any number of hosts deliver side by side, and a host that dies with a message
/// leaves it to the others. Delivery is at least once: a receiver may get a message twice, as when its answer is lost
/// on the way, and tells by the message's id.
/// </summary>
public sealed class WebhookDeliveryService
{
    // What a look found is handed out for this long, and then the run looks again: a message queued meanwhile, whose
    // first attempt goes before any retry, is not kept behind a long list of them.
    private static readonly TimeSpan LookLifetime = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly WebhookTurn _turn;
    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;

    internal WebhookDeliveryService(IServiceScopeFactory serviceScopeFactory, WebhookTurn turn,
        WebhookDeliveryOptions options, TimeProvider timeProvider)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _turn = turn;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Delivers every message that is due now: first attempts before retries, then the longest due first, a subject's
    /// in their order. It gives up those that expired or whose last attempt failed. One run at a time in a host: a
    /// second call waits for the first.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the run; an attempt under way is let go, and counts as none.</param>
    /// <returns>What the run did.</returns>
    public async Task<WebhookDeliveryResult> DeliverDue(CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken);
        try {
            return await Run(cancellationToken);
        }
        finally {
            _runLock.Release();
        }
    }

    private async Task<WebhookDeliveryResult> Run(CancellationToken cancellationToken)
    {
        var turns = new List<Task<WebhookSendKind?>>(_options.MaxConcurrentDeliveries);
        var found = new Queue<long>();
        var lookTimestamp = _timeProvider.GetTimestamp();
        var succeededCount = 0;
        var failedCount = 0;

        // whether a look may find what the last one did not: there was more than it took, or a turn has ended since,
        // which lets the next message of its subject go
        var isLookDue = true;
        try {
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();

                // A free slot takes the next message at once, whatever the others are still at: a receiver that is
                // slow to answer keeps its own slot busy, and no other message waiting.
                while (turns.Count < _options.MaxConcurrentDeliveries) {
                    var isLookNeeded = found.Count == 0
                        ? isLookDue
                        : _timeProvider.GetElapsedTime(lookTimestamp) > LookLifetime;

                    if (isLookNeeded) {
                        lookTimestamp = _timeProvider.GetTimestamp();
                        found = new Queue<long>(await FindDue(cancellationToken));
                        isLookDue = found.Count == _options.BatchSize;
                    }

                    if (found.Count == 0)
                        break;

                    turns.Add(_turn.Run(found.Dequeue(), cancellationToken));
                }

                if (turns.Count == 0)
                    break;

                var turn = await Task.WhenAny(turns);
                turns.Remove(turn);
                var kind = await turn;
                isLookDue |= kind != null;
                if (kind == WebhookSendKind.Succeeded)
                    succeededCount++;
                else if (kind is WebhookSendKind.Failed or WebhookSendKind.FailedForGood or WebhookSendKind.GivenUp)
                    failedCount++;
            }
        }
        catch {
            // A run that fails, or is cancelled, cuts off no turn: each one under way ends by itself, in its own
            // time, and writes what it has sent.
            await ((Task)Task.WhenAll(turns)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return new WebhookDeliveryResult { SucceededCount = succeededCount, FailedCount = failedCount };
    }

    private async Task<IReadOnlyList<long>> FindDue(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
        await using var session = await WebhookSession.Begin(dbContext, scope.ServiceProvider.GetService<HostDatabase>(),
            isWrite: false, cancellationToken);

        // a first attempt before any retry: a receiver that fails, and fails again, keeps no new message waiting
        return await dbContext.Messages
            .AsNoTracking()
            .Where(WebhookTurn.IsDue(dbContext, _timeProvider.GetUtcNow().UtcDateTime))
            .OrderBy(x => x.AttemptCount != 0)
            .ThenBy(x => x.NextAttemptTime)
            .ThenBy(x => x.MessageId)
            .Select(x => x.MessageId)
            .Take(_options.BatchSize)
            .ToArrayAsync(cancellationToken);
    }
}
