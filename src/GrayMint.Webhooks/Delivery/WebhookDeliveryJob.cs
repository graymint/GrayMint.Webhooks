using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMint.Webhooks.Delivery;

// Runs the delivery: at once when a message is queued in this host, and every interval for the retries and for what
// another host queued. A run that fails, as on a database that is down for a while, is logged and the next one runs:
// a stopped delivery would keep every webhook waiting without a word.
internal sealed class WebhookDeliveryJob(
    WebhookDeliveryService deliveryService,
    WebhookDeliverySignal signal,
    IOptions<WebhookDeliveryOptions> options,
    ILogger<WebhookDeliveryJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                var result = await deliveryService.DeliverDue(stoppingToken);
                if (result.SucceededCount + result.FailedCount > 0)
                    logger.LogInformation("The webhooks' delivery ran. Succeeded: {Succeeded}, Failed: {Failed}",
                        result.SucceededCount, result.FailedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception ex) {
                logger.LogError(ex, "The webhooks' delivery failed; it runs again after its interval.");
            }

            await WaitForNext(stoppingToken);
        }
    }

    private async Task WaitForNext(CancellationToken stoppingToken)
    {
        try {
            await signal.Wait(options.Value.PollInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // the loop ends
        }
    }
}
