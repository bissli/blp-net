namespace Blp.Subscriptions;

/// <summary>A consistent copy of a <see cref="Subscription"/>'s state.</summary>
/// <param name="Failure">
/// Why the subscription can no longer deliver data, or null while it can. The first
/// that holds: "session terminated" outside <see cref="Subscription.Stop"/>, "all N
/// topics failed: reason" naming the first topic's reason, or "no subscription data
/// within 60 s" of the first <see cref="Subscription.Add"/> that subscribed. A stopped
/// subscription reads only the first. The owner replaces a failed subscription.
/// </param>
/// <param name="Connected">
/// False between SessionConnectionDown and SessionConnectionUp, while the SDK
/// reconnects.
/// </param>
/// <param name="ErrorCount">
/// Exceptions caught from parsing a tick, from the tick callback, or from stopping.
/// </param>
/// <param name="LastError">"topic: type: message" of the latest one, or null.</param>
/// <param name="Topics">Held topics, in add order.</param>
public sealed record SubscriptionStatus(
    string? Failure,
    bool Connected,
    int ErrorCount,
    string? LastError,
    IReadOnlyDictionary<string, TopicState> Topics);
