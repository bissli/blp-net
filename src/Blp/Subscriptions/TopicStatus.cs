namespace Blp.Subscriptions;

/// <summary>Where one topic's subscription stands.</summary>
public enum TopicStatus
{
    /// <summary>Bloomberg has not yet answered the subscribe.</summary>
    Pending,

    /// <summary>Bloomberg sent SubscriptionStarted.</summary>
    Subscribed,

    /// <summary>
    /// Bloomberg sent SubscriptionFailure, or a SubscriptionTerminated that no
    /// <see cref="Subscription.Remove"/> caused.
    /// </summary>
    Failed,
}
