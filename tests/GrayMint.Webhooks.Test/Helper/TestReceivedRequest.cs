namespace GrayMint.Webhooks.Test.Helper;

// a request as the test receiver got it
public sealed class TestReceivedRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Body { get; init; }
}
