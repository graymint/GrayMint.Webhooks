using GrayMint.Webhooks.Delivery;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class SignatureTest
{
    // the example of the Standard Webhooks specification, which every library of it verifies
    [TestMethod]
    public void Signature_is_the_specifications()
    {
        var signature = WebhookSignature.Compute("msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, "{\"test\": 2432232314}",
            ["whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"]);

        Assert.AreEqual("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    // the specification's libraries read a secret as base64 with whsec_ before it or not, and so does the sender
    [TestMethod]
    public void Secret_without_whsec_before_it_is_the_same_secret()
    {
        var signature = WebhookSignature.Compute("msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, "{\"test\": 2432232314}",
            ["MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"]);

        Assert.AreEqual("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    [TestMethod]
    public void Signature_names_its_headers_as_the_specification_does()
    {
        Assert.AreEqual("webhook-id", WebhookSignature.IdHeader);
        Assert.AreEqual("webhook-timestamp", WebhookSignature.TimestampHeader);
        Assert.AreEqual("webhook-signature", WebhookSignature.SignatureHeader);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("whsec_")]
    [DataRow("whsec_====")]
    [DataRow("whsec_not base64!")]
    [DataRow("a secret, in plain text")]
    public void Secret_that_is_empty_or_not_base64_is_refused(string secret)
    {
        var exception = Assert.ThrowsExactly<FormatException>(
            () => WebhookSignature.Compute("msg_1", 1614265330, "{}", [secret]));

        Assert.AreEqual("A signing secret of the subscriber is empty, or is not base64.", exception.Message);
    }

    [TestMethod]
    public void Message_is_never_signed_with_no_secret()
    {
        var exception = Assert.ThrowsExactly<FormatException>(() => WebhookSignature.Compute("msg_1", 1614265330, "{}", []));

        Assert.AreEqual("The subscriber has no signing secret, and a message never goes unsigned.", exception.Message);
    }
}
