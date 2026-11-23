namespace GrayMint.Webhooks.Delivery;

// How a claimed message's turn ended.
internal enum WebhookSendKind
{
    // the receiver took it
    Succeeded,

    // an attempt failed: it goes again after its wait, unless that was its last
    Failed,

    // an attempt was answered 410 Gone: the receiver takes it no more, so it is given up
    FailedForGood,

    // no request was made and none will be: it expired, its subscriber is no more, or its address is never allowed
    GivenUp,

    // no request was made, as the host could not get it ready, by a lookup of its subscriber that failed or a turn
    // that took too long: it waits, and loses no attempt
    Postponed,

    // the host is stopping: it is let go as it was
    Canceled
}
