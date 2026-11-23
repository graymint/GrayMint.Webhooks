using GrayMint.Webhooks.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GrayMint.Webhooks.Test.Tests;

[TestClass]
public class OptionsTest
{
    [TestMethod]
    public void Registration_refuses_an_option_that_is_wrong()
    {
        var refused = new (Action<WebhookDeliveryOptions> Configure, string Message)[] {
            (x => x.PollInterval = TimeSpan.Zero, "The poll interval is above zero."),
            (x => x.AttemptTimeout = TimeSpan.Zero, "An attempt's timeout is above zero."),
            (x => x.RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-1)], "A wait before a retry is not below zero."),
            (x => x.MaxConcurrentDeliveries = 0, "At least one attempt runs at a time."),
            (x => x.BatchSize = 0, "A look takes at least one message."),
            (x => x.UserAgent = "", "The user agent can't be a User-Agent header: ''."),
            (x => x.UserAgent = "two\nlines", "The user agent can't be a User-Agent header: 'two\nlines'.")
        };

        foreach (var (configure, message) in refused) {
            var exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ServiceCollection().AddGrayMintWebhookDelivery(configure));

            StringAssert.StartsWith(exception.Message, message);
        }
    }

    [TestMethod]
    public void Delivery_follows_the_schedule_the_specification_suggests_unless_told_otherwise()
    {
        var services = new ServiceCollection().AddGrayMintWebhookDelivery();
        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;

        CollectionAssert.AreEqual(
            new[] {
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
                TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10)
            }, options.RetryDelays.ToArray());

        Assert.IsFalse(options.AllowInsecureTargets, "a host's own network is closed to its subscribers unless a test opens it");
    }
}
