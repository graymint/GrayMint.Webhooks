namespace GrayMint.Webhooks.Persistence.Models;

// One try to deliver a message, written with what it did to the message, and never changed.
internal sealed class AttemptModel
{
    public long AttemptId { get; set; }
    public required long MessageId { get; init; }
    public required DateTime AttemptTime { get; init; }
    public required int DurationMilliseconds { get; init; }
    public required bool IsSuccess { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
}
