using Bloomberglp.Blpapi;
using Blp.Internal;
using Blp.Subscriptions;
using Microsoft.Extensions.Time.Testing;
using SdkSubscription = Bloomberglp.Blpapi.Subscription;

namespace Blp.Tests.Subscriptions;

/// <summary>
/// A <see cref="Blp.Subscriptions.Subscription"/> of LAST_PRICE and BID on a recording
/// session, with a fake clock and a delivery thread for events.
/// </summary>
internal sealed class SubscriptionHarness
{
    private Bloomberglp.Blpapi.EventHandler? _handler;

    /// <summary>Starts the subscription.</summary>
    /// <param name="onTick">The tick callback. Null records each tick in Ticks.</param>
    /// <param name="session">The session. Null takes a new recording session.</param>
    /// <param name="options">Subscription settings. Null takes the defaults.</param>
    public SubscriptionHarness(
        Action<string, IReadOnlyDictionary<string, object?>>? onTick = null,
        RecordingSession? session = null,
        SubscriptionOptions? options = null)
    {
        Session = session ?? new RecordingSession();
        Subscription = Blp.Subscriptions.Subscription.Start(
            ["LAST_PRICE", "BID"],
            onTick ?? ((topic, row) => Ticks.Add((topic, row))),
            options ?? new SubscriptionOptions(),
            (_, handler) =>
            {
                _handler = handler;
                return Session;
            },
            Clock);
    }

    /// <summary>The clock. Each test sets it by advancing from its start.</summary>
    public FakeTimeProvider Clock { get; } = new();

    /// <summary>The recording session.</summary>
    public RecordingSession Session { get; }

    /// <summary>The subscription under test.</summary>
    public Blp.Subscriptions.Subscription Subscription { get; }

    /// <summary>Ticks the default callback received, in order.</summary>
    public List<(string Topic, IReadOnlyDictionary<string, object?> Row)> Ticks { get; } = [];

    /// <summary>Sets the clock to seconds past its start.</summary>
    /// <param name="seconds">
    /// Seconds past the start, never less than the last set.
    /// </param>
    public void At(double seconds) => Clock.SetUtcNow(Clock.Start.AddSeconds(seconds));

    /// <summary>Runs the event handler over the events on one new thread.</summary>
    /// <param name="events">The events, in order.</param>
    public void Deliver(params Event[] events)
    {
        var thread = new Thread(() =>
        {
            foreach (var sessionEvent in events)
            {
                Handle(sessionEvent);
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "handler hung");
    }

    /// <summary>Runs the event handler on the calling thread.</summary>
    /// <param name="sessionEvent">The event.</param>
    public void Handle(Event sessionEvent) =>
        (_handler ?? throw new InvalidOperationException("No handler."))(sessionEvent, null);

    /// <summary>Topic to correlation id of one Subscribe call.</summary>
    /// <param name="callIndex">The call. Negative counts from the last.</param>
    /// <returns>The ids, in subscribe order.</returns>
    public Dictionary<string, CorrelationID> Ids(int callIndex = -1)
    {
        var calls = Session.SubscribeCalls;
        var call = calls[callIndex < 0 ? calls.Count + callIndex : callIndex];
        return call.ToDictionary(
            subscription => subscription.SubscriptionString.Split('?')[0],
            subscription => subscription.CorrelationID);
    }
}
