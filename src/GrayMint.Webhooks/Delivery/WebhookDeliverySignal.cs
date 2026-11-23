namespace GrayMint.Webhooks.Delivery;

// Wakes the host's delivery before its next look: a message has just been queued here. Set any number of times before
// the delivery wakes, it wakes it once. In a host that doesn't deliver nothing waits on it.
internal sealed class WebhookDeliverySignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);
    private readonly Lock _lock = new();

    public void Set()
    {
        lock (_lock) {
            if (_semaphore.CurrentCount == 0)
                _semaphore.Release();
        }
    }

    public Task Wait(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _semaphore.WaitAsync(timeout, cancellationToken);
    }
}
