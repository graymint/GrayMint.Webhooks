using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Helper;

// a line the module logged
public sealed class TestLogEntry
{
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }
    public required Exception? Exception { get; init; }
}
