using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;
using Blp.Subscriptions;
using Microsoft.Extensions.Time.Testing;
using Subscription = Blp.Subscriptions.Subscription;

namespace Blp.Tests.Subscriptions;

/// <summary>
/// <see cref="Subscription"/> over excerpts of events a terminal sent, on the real
/// //blp/mktdata shape.
/// </summary>
public class SubscriptionCaptureTests
{
    private const string Ibm = "IBM US Equity";
    private const string Spx = "SPX Index";
    private const string NotATicker = "NOTATICKER US Equity";

    private static readonly string[] _fields =
        ["LAST_PRICE", "BID", "ASK", "VOLUME", "TIME", "LAST_TRADE"];

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A tick row holds only the fields the message sent, typed as Bloomberg typed
    /// them: prices as floats, VOLUME as a long, and TIME as a time of day.
    /// </summary>
    /// <remarks>
    /// Mutation: a field the message lacks added to the row as null, VOLUME read as a
    /// float, or TIME left as text.
    /// Oracle: three IBM messages of the subscription capture: the initial paint
    /// with all six fields, a trade with VOLUME only, and a bid quote with BID only.
    /// The TIME value is a wall clock read in the clock's zone, so it comes back
    /// unshifted.
    /// </remarks>
    [Fact]
    public void TickRowHoldsOnlyTheFieldsSent()
    {
        var run = new Run();
        run.Subscription.Add(Ibm);
        var id = run.Ids()[Ibm];

        Action<IMessageFormatter> initialPaint = tick =>
        {
            SetEventType(tick, "SUMMARY", "INITPAINT");
            tick.SetElement(Name.GetName("BID"), 232.73);
            tick.SetElement(Name.GetName("ASK"), 232.86);
            tick.SetElement(Name.GetName("LAST_PRICE"), 232.795);
            tick.SetElement(Name.GetName("VOLUME"), 2109015L);
            tick.SetElement(Name.GetName("LAST_TRADE"), 232.795);
            tick.SetElement(
                Name.GetName("TIME"), new Datetime { Hour = 13, Minute = 44, Second = 16 });
        };
        Action<IMessageFormatter> trade = tick =>
        {
            SetEventType(tick, "TRADE", "NEW");
            tick.SetElement(Name.GetName("VOLUME"), 2109016L);
        };
        Action<IMessageFormatter> bidQuote = tick =>
        {
            SetEventType(tick, "QUOTE", "BID");
            tick.SetElement(Name.GetName("BID"), 233.04);
        };

        run.Deliver(CapturedMarketDataEvents.Data((id, initialPaint), (id, trade), (id, bidQuote)));

        Assert.Equal([Ibm, Ibm, Ibm], run.Ticks.Select(tick => tick.Topic));
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["LAST_PRICE"] = 232.795,
                ["BID"] = 232.73,
                ["ASK"] = 232.86,
                ["VOLUME"] = 2109015L,
                ["TIME"] = new TimeOnly(13, 44, 16),
                ["LAST_TRADE"] = 232.795,
            },
            run.Ticks[0].Row);
        Assert.Equal(
            new Dictionary<string, object?> { ["VOLUME"] = 2109016L }, run.Ticks[1].Row);
        Assert.Equal(new Dictionary<string, object?> { ["BID"] = 233.04 }, run.Ticks[2].Row);
    }

    /// <summary>
    /// A tick's date-time is read in the clock's local zone and returned in it.
    /// </summary>
    /// <remarks>
    /// Mutation: the zone taken from TimeZoneInfo.Local in place of the clock, for the
    /// read or for the result.
    /// Oracle: 13:44:16 on 2026-11-17 in Tokyo is 13:44:16 +09:00, worked by hand.
    /// Tokyo differs from any host zone in use and has no daylight saving.
    /// </remarks>
    [Fact]
    public void TickDateTimeTakesTheClockZone()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 5, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        var run = new Run(clock: clock);
        run.Subscription.Add(Ibm);
        var id = run.Ids()[Ibm];

        Action<IMessageFormatter> trade = tick =>
        {
            SetEventType(tick, "TRADE", "NEW");
            tick.SetElement(Name.GetName("TIME"), new Datetime(2026, 11, 17, 13, 44, 16, 0));
        };

        run.Deliver(CapturedMarketDataEvents.Data((id, trade)));

        var time = Assert.IsType<DateTimeOffset>(Assert.Single(run.Ticks).Row["TIME"]);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 13, 44, 16, TimeSpan.FromHours(9)), time);
        Assert.Equal(TimeSpan.FromHours(9), time.Offset);
    }

    /// <summary>
    /// A SubscriptionFailure marks only its topic failed, with Bloomberg's description,
    /// and the topics that started stay subscribed.
    /// </summary>
    /// <remarks>
    /// Mutation: a failure marking every topic failed, the category read in place of
    /// the description, or a started topic left pending.
    /// Oracle: the subscription capture, where NOTATICKER US Equity failed with
    /// BAD_SEC before IBM and SPX started. The failure's source element is left out of
    /// the excerpt.
    /// </remarks>
    [Fact]
    public void FailureOfAnUnknownTickerLeavesTheOthersSubscribed()
    {
        var run = new Run();
        run.Subscription.Add(Ibm, Spx, NotATicker);
        var ids = run.Ids();

        run.Deliver(
            MarketDataEvents.Status((
                ids[NotATicker],
                """
                <SubscriptionFailure>
                  <reason>
                    <errorCode>2</errorCode>
                    <category>BAD_SEC</category>
                    <description>Unknown/Invalid Security</description>
                    <subcategory></subcategory>
                  </reason>
                </SubscriptionFailure>
                """)),
            MarketDataEvents.Status(
                MarketDataEvents.Started(ids[Ibm]), MarketDataEvents.Started(ids[Spx])));

        var status = run.Subscription.GetStatus();
        Assert.Equal(
            [
                (Ibm, TopicStatus.Subscribed, (string?)null),
                (Spx, TopicStatus.Subscribed, null),
                (NotATicker, TopicStatus.Failed, "Unknown/Invalid Security"),
            ],
            status.Topics.Values.Select(topic => (topic.Topic, topic.Status, topic.Reason)));
        Assert.Null(status.Failure);
    }

    /// <summary>
    /// The CANCELLED terminations Bloomberg sends after Stop unsubscribes leave the
    /// topics as they were and raise no failure or error.
    /// </summary>
    /// <remarks>
    /// Mutation: the id mapping kept through Stop, which marks each topic failed with
    /// "Subscription cancelled", or a failure reported for the stop.
    /// Oracle: the subscription capture, where Stop was followed by a
    /// SubscriptionTerminated with category CANCELLED for each topic.
    /// </remarks>
    [Fact]
    public void StopIgnoresTheCancelledTerminationsItCauses()
    {
        var run = new Run();
        run.Subscription.Add(Ibm, Spx);
        var ids = run.Ids();
        run.Deliver(MarketDataEvents.Status(
            MarketDataEvents.Started(ids[Ibm]), MarketDataEvents.Started(ids[Spx])));
        run.Session.OnBeginStop = () => new Thread(() => run.Handle(
            MarketDataEvents.Status(Cancelled(ids[Ibm]), Cancelled(ids[Spx])),
            MarketDataEvents.Session("SessionTerminated"))).Start();

        bool stopped = run.Subscription.Stop(_wait);

        Assert.True(stopped);
        Assert.Equal(
            [ids[Ibm], ids[Spx]],
            run.Session.UnsubscribeCalls.Single().Select(
                subscription => subscription.CorrelationID));
        var status = run.Subscription.GetStatus();
        Assert.Equal(
            [TopicStatus.Subscribed, TopicStatus.Subscribed],
            status.Topics.Values.Select(topic => topic.Status));
        Assert.Null(status.Failure);
        Assert.Equal(0, status.ErrorCount);
    }

    /// <summary>
    /// An interval subscription sends Bloomberg's interval option, and an INTERVAL
    /// summary yields a row like any other tick.
    /// </summary>
    /// <remarks>
    /// Mutation: the interval sent in whole seconds ("interval=5") or not at all, or
    /// an INTERVAL message ignored.
    /// Oracle: the subscription_interval capture, whose options were interval=5.0, and
    /// its first IBM INTERVAL summary.
    /// </remarks>
    [Fact]
    public void IntervalSubscriptionSendsTheOptionAndYieldsSummaryRows()
    {
        var run = new Run(new SubscriptionOptions { Interval = TimeSpan.FromSeconds(5) });
        run.Subscription.Add(Ibm);
        var id = run.Ids()[Ibm];

        Action<IMessageFormatter> summary = tick =>
        {
            SetEventType(tick, "SUMMARY", "INTERVAL");
            tick.SetElement(Name.GetName("LAST_PRICE"), 233.04);
            tick.SetElement(Name.GetName("BID"), 232.96);
            tick.SetElement(Name.GetName("ASK"), 233.17);
            tick.SetElement(Name.GetName("VOLUME"), 2119500L);
            tick.SetElement(
                Name.GetName("TIME"), new Datetime { Hour = 13, Minute = 45, Second = 26 });
            tick.SetElement(Name.GetName("LAST_TRADE"), 233.04);
        };

        run.Deliver(CapturedMarketDataEvents.Data((id, summary)));

        var subscribed = run.Session.SubscribeCalls.Single().Single();
        Assert.EndsWith("&interval=5.0", subscribed.SubscriptionString);
        var (topic, row) = Assert.Single(run.Ticks);
        Assert.Equal(Ibm, topic);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["LAST_PRICE"] = 233.04,
                ["BID"] = 232.96,
                ["ASK"] = 233.17,
                ["VOLUME"] = 2119500L,
                ["TIME"] = new TimeOnly(13, 45, 26),
                ["LAST_TRADE"] = 233.04,
            },
            row);
    }

    private static void SetEventType(
        IMessageFormatter tick, string type, string subtype)
    {
        tick.SetElement(Name.GetName("MKTDATA_EVENT_TYPE"), type);
        tick.SetElement(Name.GetName("MKTDATA_EVENT_SUBTYPE"), subtype);
    }

    private static (CorrelationID Id, string Xml) Cancelled(CorrelationID id) => (
        id,
        """
        <SubscriptionTerminated>
          <reason>
            <source>SubscriptionManager</source>
            <errorCode>0</errorCode>
            <category>CANCELLED</category>
            <description>Subscription cancelled</description>
            <subcategory></subcategory>
          </reason>
        </SubscriptionTerminated>
        """);

    /// <summary>
    /// A subscription of the six captured fields on a recording session, with a fake
    /// clock.
    /// </summary>
    private sealed class Run
    {
        private Bloomberglp.Blpapi.EventHandler? _handler;

        /// <summary>Starts the subscription.</summary>
        /// <param name="options">
        /// Subscription settings. Null takes the defaults.
        /// </param>
        /// <param name="clock">
        /// The clock. Null takes a new fake clock, whose local zone is UTC.
        /// </param>
        public Run(SubscriptionOptions? options = null, FakeTimeProvider? clock = null) =>
            Subscription = Subscription.Start(
                _fields,
                (topic, row) => Ticks.Add((topic, row)),
                options ?? new SubscriptionOptions(),
                (_, handler) =>
                {
                    _handler = handler;
                    return Session;
                },
                clock ?? new FakeTimeProvider());

        public RecordingSession Session { get; } = new();

        public Subscription Subscription { get; }

        public List<(string Topic, IReadOnlyDictionary<string, object?> Row)> Ticks { get; } = [];

        /// <summary>Runs the event handler over the events on one new thread.</summary>
        /// <param name="events">The events, in order.</param>
        public void Deliver(params Event[] events)
        {
            var thread = new Thread(() => Handle(events));
            thread.Start();
            Assert.True(thread.Join(_wait), "handler hung");
        }

        /// <summary>Runs the event handler on the calling thread.</summary>
        /// <param name="events">The events, in order.</param>
        public void Handle(params Event[] events)
        {
            var handler = _handler ?? throw new InvalidOperationException("No handler.");
            foreach (var sessionEvent in events)
            {
                handler(sessionEvent, null);
            }
        }

        /// <summary>Topic to correlation id of the one Subscribe call so far.</summary>
        /// <returns>The ids, in subscribe order.</returns>
        public Dictionary<string, CorrelationID> Ids() => Session.SubscribeCalls
            .Last()
            .ToDictionary(
                subscription => subscription.SubscriptionString.Split('?')[0],
                subscription => subscription.CorrelationID);
    }
}
