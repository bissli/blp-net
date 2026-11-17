namespace Blp.Subscriptions;

/// <summary>
/// Settings for <see cref="Subscription.Start(IReadOnlyList{string},
/// Action{string, IReadOnlyDictionary{string, object}}, SubscriptionOptions)"/>.
/// </summary>
public sealed record SubscriptionOptions
{
    /// <summary>
    /// Bloomberg application identity key. Null connects without one.
    /// </summary>
    public string? ApplicationIdentityKey { get; init; }

    /// <summary>
    /// Time between conflated updates, sent to the tenth of a second. Zero leaves
    /// updates unconflated.
    /// </summary>
    public TimeSpan Interval { get; init; }
}
