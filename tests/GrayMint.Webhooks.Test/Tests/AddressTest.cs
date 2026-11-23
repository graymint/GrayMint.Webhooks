using System.Net;
using GrayMint.Webhooks.Delivery;
using GrayMint.Webhooks.Messages;
using GrayMint.Webhooks.Test.Helper;

namespace GrayMint.Webhooks.Test.Tests;

// A message goes by https to a public address alone, so a subscriber can't aim one at the host's own network.
[TestClass]
public class AddressTest(TestContext testContext)
{
    private CancellationToken CancellationToken => testContext.CancellationToken;

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("1.1.1.1")]
    [DataRow("172.32.0.1")]
    [DataRow("100.128.0.1")]
    [DataRow("192.169.0.1")]
    [DataRow("2606:4700:4700::1111")]
    [DataRow("2001:4860:4860::8888")]
    [DataRow("2a00:1450:4001::1")]
    [DataRow("2620:fe::fe")]
    [DataRow("::ffff:8.8.8.8")]
    [DataRow("64:ff9b::808:808")]
    [DataRow("2002:808:808::1")]
    public void Address_of_the_internet_is_public(string address)
    {
        Assert.IsTrue(WebhookAddressPolicy.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    [DataRow("0.0.0.0")]
    [DataRow("127.0.0.1")]
    [DataRow("127.255.255.254")]
    [DataRow("10.1.2.3")]
    [DataRow("100.64.0.1")]
    [DataRow("169.254.169.254")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.1.1")]
    [DataRow("192.0.2.1")]
    [DataRow("198.18.0.1")]
    [DataRow("224.0.0.1")]
    [DataRow("255.255.255.255")]
    [DataRow("::")]
    [DataRow("::1")]
    [DataRow("fe80::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456::1")]
    [DataRow("ff02::1")]
    [DataRow("2001:db8::1")]
    [DataRow("2001::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("64:ff9b::7f00:1")]
    [DataRow("64:ff9b::a00:1")]
    [DataRow("2002:7f00:1::1")]
    [DataRow("2002:c0a8:101::1")]
    [DataRow("::127.0.0.1")]
    [DataRow("::ffff:0:7f00:1")]
    [DataRow("64:ff9b:1::1")]
    [DataRow("100::1")]
    [DataRow("2001:10::1")]
    [DataRow("3fff::1")]
    [DataRow("4000::1")]
    [DataRow("5f00::1")]
    [DataRow("e000::1")]
    [DataRow("fec0::1")]
    public void Address_of_this_machine_a_private_network_or_nobody_is_not_public(string address)
    {
        Assert.IsFalse(WebhookAddressPolicy.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    public void Targets_of_a_host_are_its_public_addresses_less_this_machines_own()
    {
        // 8.8.8.8 stands for a public address of this machine
        var ownAddresses = new HashSet<IPAddress> { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111") };
        IPAddress[] addresses = [
            IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:8.8.8.8"),
            IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("::1")
        ];

        var targets = WebhookAddressPolicy.GetTargets("shop.example.com", addresses, ownAddresses);

        CollectionAssert.AreEqual(new[] { IPAddress.Parse("1.1.1.1") }, targets.ToArray());
    }

    [TestMethod]
    public void Host_with_no_address_a_message_may_go_to_is_refused()
    {
        var ownAddresses = new HashSet<IPAddress> { IPAddress.Parse("8.8.8.8") };

        var inside = Assert.ThrowsExactly<HttpRequestException>(() => WebhookAddressPolicy.GetTargets(
            "inside.example.com", [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("::1")], ownAddresses));

        var own = Assert.ThrowsExactly<HttpRequestException>(() => WebhookAddressPolicy.GetTargets(
            "own.example.com", [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:8.8.8.8")],
            ownAddresses));

        Assert.AreEqual("The host inside.example.com has no public address.", inside.Message);
        Assert.AreEqual("The host own.example.com is the sender's own machine.", own.Message);
    }

    [TestMethod]
    public void Own_addresses_are_those_of_this_machines_network_interfaces()
    {
        var ownAddresses = WebhookAddressPolicy.GetOwnAddresses();

        Assert.IsTrue(ownAddresses.Contains(IPAddress.Loopback));
        Assert.IsFalse(ownAddresses.Contains(IPAddress.Parse("8.8.8.8")));
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Message_to_an_address_that_is_never_allowed_is_given_up_without_a_request(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken,
            options => options.AllowInsecureTargets = false);

        await using var receiver = await TestReceiver.Start(CancellationToken);
        var port = receiver.BaseUri.Port;
        var byHttp = await host.Queue(receiver.Url("/webhooks"), CancellationToken);
        var toThisMachine = await host.Queue(new Uri($"https://127.0.0.1:{port}/webhooks"), CancellationToken);
        var toThisMachineV6 = await host.Queue(new Uri($"https://[::1]:{port}/webhooks"), CancellationToken);
        var toPrivateNetwork = await host.Queue(new Uri("https://169.254.169.254/latest/meta-data"), CancellationToken);

        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(4, result.FailedCount);
        Assert.AreEqual(0, receiver.Requests.Count);
        await AssertGivenUp(host, byHttp, "The address is not https.");
        await AssertGivenUp(host, toThisMachine, "The address is not a public one.");
        await AssertGivenUp(host, toThisMachineV6, "The address is not a public one.");
        await AssertGivenUp(host, toPrivateNetwork, "The address is not a public one.");
    }

    [TestMethod]
    [EveryDatabase]
    public async Task Name_that_leads_to_no_public_address_is_refused_as_the_sender_connects(TestDatabaseProvider provider)
    {
        await using var host = await TestWebhookHost.Create(provider, CancellationToken,
            options => options.AllowInsecureTargets = false);

        await using var receiver = await TestReceiver.Start(CancellationToken);

        // the name passes the look at the address, which has no number to judge; its addresses are this machine's
        var message = await host.Queue(new Uri($"https://localhost:{receiver.BaseUri.Port}/webhooks"), CancellationToken);
        var result = await host.Deliver(CancellationToken);

        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(0, receiver.Requests.Count);
        var attempt = (await host.GetAttempts(message.MessageId, CancellationToken)).Single();
        Assert.IsFalse(attempt.IsSuccess);
        Assert.AreEqual("The host localhost has no public address.", attempt.Error);

        // a name's addresses change, so it is tried again
        Assert.AreEqual(WebhookMessageStatus.Pending, (await host.Get(message.MessageId, CancellationToken)).Status);
    }

    private async Task AssertGivenUp(TestWebhookHost host, WebhookMessage message, string error)
    {
        var givenUp = await host.Get(message.MessageId, CancellationToken);
        Assert.AreEqual(WebhookMessageStatus.Failed, givenUp.Status);
        Assert.AreEqual(error, givenUp.LastError);
        Assert.AreEqual(0, givenUp.AttemptCount);
        Assert.AreEqual(0, (await host.GetAttempts(message.MessageId, CancellationToken)).Count);
    }
}
