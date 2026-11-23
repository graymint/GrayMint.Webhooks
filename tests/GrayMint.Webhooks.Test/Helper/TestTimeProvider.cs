namespace GrayMint.Webhooks.Test.Helper;

// A clock a test moves itself, for waits measured in hours. Its timers are the system's.
public sealed class TestTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _now;
    }

    public void Advance(TimeSpan time)
    {
        lock (_lock)
            _now += time;
    }
}
