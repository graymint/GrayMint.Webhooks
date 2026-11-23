namespace GrayMint.Webhooks.Delivery;

/// <summary>How a host delivers.</summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>
    /// How often the host looks for due messages. A message queued in this host goes sooner; the look is for the
    /// retries, and for what another host queued.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one attempt may take, from the address lookup to the receiver's status line.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    // How long a message's turn has to get to its request, with its claim and the host's lookup of the subscriber:
    // a turn that takes longer makes no request, and the message waits. No option of a host's: the tests shorten it.
    internal TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The wait after each failed attempt, so one more attempt than there are waits; a message whose last one fails
    /// is given up. The default is the schedule the Standard Webhooks specification suggests: eight attempts over
    /// some 28 hours.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [
        TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
        TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10)
    ];

    /// <summary>The most attempts under way at once in this host.</summary>
    public int MaxConcurrentDeliveries { get; set; } = 8;

    /// <summary>The most due messages one look takes; it looks again at once while there are more.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The <c>User-Agent</c> of every request.</summary>
    public string UserAgent { get; set; } = "GrayMint.Webhooks";

    /// <summary>
    /// For development and tests alone: messages also go by http, and to this machine and private networks. Outside
    /// them a subscriber could aim a message at the host's own machine or network.
    /// </summary>
    public bool AllowInsecureTargets { get; set; }

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentException("The poll interval is above zero.", nameof(PollInterval));

        if (AttemptTimeout <= TimeSpan.Zero)
            throw new ArgumentException("An attempt's timeout is above zero.", nameof(AttemptTimeout));

        ArgumentNullException.ThrowIfNull(RetryDelays);
        if (RetryDelays.Any(x => x < TimeSpan.Zero))
            throw new ArgumentException("A wait before a retry is not below zero.", nameof(RetryDelays));

        if (MaxConcurrentDeliveries < 1)
            throw new ArgumentException("At least one attempt runs at a time.", nameof(MaxConcurrentDeliveries));

        if (BatchSize < 1)
            throw new ArgumentException("A look takes at least one message.", nameof(BatchSize));

        // as a request would refuse it, at every attempt
        using var request = new HttpRequestMessage();
        if (string.IsNullOrWhiteSpace(UserAgent) || !request.Headers.UserAgent.TryParseAdd(UserAgent))
            throw new ArgumentException($"The user agent can't be a User-Agent header: '{UserAgent}'.", nameof(UserAgent));
    }
}
