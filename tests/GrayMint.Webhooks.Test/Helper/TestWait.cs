namespace GrayMint.Webhooks.Test.Helper;

// waits for what another thread does, by looking again, never by a fixed sleep
public static class TestWait
{
    public static async Task Until(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition()) {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Waited 20 seconds, and still not: {what}.");

            await Task.Delay(50, cancellationToken);
        }
    }
}
