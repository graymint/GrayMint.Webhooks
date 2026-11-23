using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Helper;

// A receiver of webhooks: a real server on a port of this machine, which keeps what it got and answers as the test
// tells it to.
public sealed class TestReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<TestReceivedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, int> _pathStatusCodes = new();

    private TestReceiver(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseUri => new(_app.Urls.Single());
    public IReadOnlyList<TestReceivedRequest> Requests => [.. _requests];

    // what it answers, to any path that has no status of its own
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    // where a 3xx answer sends the sender
    public string? RedirectLocation { get; set; }

    // how long it takes to answer
    public TimeSpan Delay { get; set; }

    // runs after a request is kept and before it is answered
    public Func<TestReceivedRequest, Task>? BeforeAnswer { get; set; }

    public static Task<TestReceiver> Start(CancellationToken cancellationToken)
    {
        return Start(IPAddress.Loopback, cancellationToken);
    }

    // on one address of this machine alone, as 127.0.0.1 or ::1
    public static async Task<TestReceiver> Start(IPAddress address, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // port 0: the system picks a free one
        builder.WebHost.UseUrls($"http://{new IPEndPoint(address, 0)}");
        var app = builder.Build();
        var receiver = new TestReceiver(app);
        app.Run(receiver.Handle);
        await app.StartAsync(cancellationToken);
        return receiver;
    }

    public Uri Url(string path)
    {
        return new Uri(BaseUri, path);
    }

    public void SetStatusCode(string path, int statusCode)
    {
        _pathStatusCodes[path] = statusCode;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    private async Task Handle(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var request = new TestReceivedRequest {
            Method = context.Request.Method,
            Path = context.Request.Path,
            Headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Body = await reader.ReadToEndAsync(context.RequestAborted)
        };

        _requests.Enqueue(request);
        if (BeforeAnswer != null)
            await BeforeAnswer(request);

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, context.RequestAborted);

        context.Response.StatusCode = _pathStatusCodes.GetValueOrDefault(context.Request.Path, StatusCode);
        if (RedirectLocation != null)
            context.Response.Headers.Location = RedirectLocation;
    }
}
