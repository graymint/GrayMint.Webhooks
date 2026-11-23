namespace GrayMint.Webhooks.Test.Helper;

// a row of the host's own, which a webhook tells of
public sealed class TestOrder
{
    public int TestOrderId { get; set; }
    public required string State { get; set; }
}
