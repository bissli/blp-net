namespace Blp.Subscriptions;

/// <summary>
/// One topic's state, as <see cref="Subscription.GetStatus"/> copies it.
/// </summary>
/// <param name="Topic">The topic, such as "IBM US Equity".</param>
public sealed record TopicState(string Topic)
{
    /// <summary>Where the subscription stands.</summary>
    public TopicStatus Status { get; init; } = TopicStatus.Pending;

    /// <summary>Bloomberg's description of a failed topic, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>When SubscriptionStarted arrived, or null.</summary>
    public DateTimeOffset? SubscribedAt { get; init; }

    /// <summary>When the first tick, the initial snapshot, arrived, or null.</summary>
    public DateTimeOffset? FirstDataAt { get; init; }

    /// <summary>When the latest tick arrived, or null.</summary>
    public DateTimeOffset? LastTickAt { get; init; }

    /// <summary>
    /// Ticks that arrived less than 60 seconds before the status read.
    /// </summary>
    public int TicksLastMinute { get; init; }
}
