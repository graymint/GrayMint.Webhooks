using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GrayMint.Webhooks.Persistence.Models;

namespace GrayMint.Webhooks.Delivery;

// Sends one message: a POST of its payload, signed, to its address, and says how the receiver took it.
// The client is the sender's own, so no setting of a host can lift what guards the host's network: every address is
// judged as the socket connects to it, a redirect is an answer and never followed, and no cookie or proxy is used.
internal sealed class WebhookSender : IDisposable
{
    // what a subscriber's headers can't replace
    private static readonly HashSet<string> OwnHeaders = new(StringComparer.OrdinalIgnoreCase) {
        WebhookSignature.IdHeader, WebhookSignature.TimestampHeader, WebhookSignature.SignatureHeader, "User-Agent", "Host"
    };

    // the machine's own addresses are kept this long: listing them takes too long to do for every connection
    private static readonly TimeSpan OwnAddressesLifetime = TimeSpan.FromSeconds(30);

    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _httpClient;
    private readonly Lock _ownAddressesLock = new();
    private IReadOnlySet<IPAddress> _ownAddresses = new HashSet<IPAddress>();
    private long? _ownAddressesTimestamp;

    public WebhookSender(WebhookDeliveryOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _httpClient = new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            // a name's addresses are looked up again for a new connection
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = Connect
        }) {
            // each attempt has its own
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<WebhookSendResult> Send(MessageModel message, WebhookSubscriber subscriber,
        CancellationToken cancellationToken)
    {
        var attemptTime = _timeProvider.GetUtcNow();
        var startTimestamp = _timeProvider.GetTimestamp();
        var url = new Uri(message.Url);
        var refusal = WebhookAddressPolicy.FindRefusal(url, _options.AllowInsecureTargets);
        if (refusal != null)
            return new WebhookSendResult { Kind = WebhookSendKind.GivenUp, Error = refusal };

        // one timeout for the whole attempt: the address lookup, the connect and the receiver's answer
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.AttemptTimeout);
        try {
            using var request = CreateRequest(message, url, subscriber, attemptTime);

            // only the status counts: the receiver's body is never read
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return Attempted(WebhookSendKind.Succeeded, statusCode, null);

            return Attempted(
                response.StatusCode == HttpStatusCode.Gone ? WebhookSendKind.FailedForGood : WebhookSendKind.Failed,
                statusCode, $"The receiver answered {statusCode} ({response.StatusCode}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return new WebhookSendResult { Kind = WebhookSendKind.Canceled };
        }
        catch (OperationCanceledException) {
            return Attempted(WebhookSendKind.Failed, null,
                $"The receiver did not answer within {_options.AttemptTimeout.TotalSeconds:0.###} seconds.");
        }
        catch (Exception ex) {
            // whatever stopped the request is the attempt's failure: its cause says most
            return Attempted(WebhookSendKind.Failed, null, ex.GetBaseException().Message);
        }

        WebhookSendResult Attempted(WebhookSendKind kind, int? statusCode, string? error)
        {
            return new WebhookSendResult {
                Kind = kind,
                AttemptTime = attemptTime.UtcDateTime,
                Duration = _timeProvider.GetElapsedTime(startTimestamp),
                StatusCode = statusCode,
                Error = error
            };
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private HttpRequestMessage CreateRequest(MessageModel message, Uri url, WebhookSubscriber subscriber,
        DateTimeOffset attemptTime)
    {
        var messageId = WebhookIds.FormatMessageId(message.Uid);
        var unixSeconds = attemptTime.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, url) {
            // never HTTP/3: it connects by another way than the handler's connect, where an address is judged
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        try {
            request.Content = new StringContent(message.Payload, Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            foreach (var (name, value) in subscriber.Headers) {
                if (OwnHeaders.Contains(name))
                    throw new FormatException($"A subscriber's header can't be {name}, which is the sender's own.");

                request.Headers.Add(name, value);
            }

            request.Headers.Add(WebhookSignature.IdHeader, messageId);
            request.Headers.Add(WebhookSignature.TimestampHeader, unixSeconds.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(WebhookSignature.SignatureHeader,
                WebhookSignature.Compute(messageId, unixSeconds, message.Payload, subscriber.SigningSecrets));

            return request;
        }
        catch {
            request.Dispose();
            throw;
        }
    }

    // The check that holds: the addresses are looked up here and the socket goes to one of them, so a name can't
    // answer with a public address for a check and a private one for the connect.
    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0)
            throw new HttpRequestException($"The host {host} has no address.");

        var targets = _options.AllowInsecureTargets
            ? addresses
            : WebhookAddressPolicy.GetTargets(host, addresses, GetOwnAddresses());

        return await ConnectToFirst(targets, context.DnsEndPoint.Port, cancellationToken);
    }

    private IReadOnlySet<IPAddress> GetOwnAddresses()
    {
        lock (_ownAddressesLock) {
            if (_ownAddressesTimestamp == null ||
                _timeProvider.GetElapsedTime(_ownAddressesTimestamp.Value) > OwnAddressesLifetime) {
                _ownAddresses = WebhookAddressPolicy.GetOwnAddresses();
                _ownAddressesTimestamp = _timeProvider.GetTimestamp();
            }

            return _ownAddresses;
        }
    }

    // each address in turn, until one takes the connection
    private static async Task<Stream> ConnectToFirst(IReadOnlyList<IPAddress> addresses, int port,
        CancellationToken cancellationToken)
    {
        for (var i = 0;; i++) {
            var socket = new Socket(addresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try {
                await socket.ConnectAsync(new IPEndPoint(addresses[i], port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (i < addresses.Count - 1) {
                socket.Dispose();
            }
            catch {
                socket.Dispose();
                throw;
            }
        }
    }
}
