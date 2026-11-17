using Bloomberglp.Blpapi;
using Blp.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using static Blp.Tests.Subscriptions.MarketDataEvents;

namespace Blp.Tests.Subscriptions;

/// <summary>The log lines a <see cref="Subscription"/> writes to its logger.</summary>
public class SubscriptionLoggingTests
{
    private const string Aaa = "AAA US Equity";
    private const string Bbb = "BBB US Equity";

    /// <summary>
    /// A failed or terminated held topic is an error naming the message type, topic and
    /// reason. A started topic, and a removed topic's termination, log nothing.
    /// </summary>
    /// <remarks>
    /// Mutation: SubscriptionStarted logged, or a message for an unknown id logged.
    /// Oracle: one status event holding a start, a failure, and the termination of a
    /// topic removed before it.
    /// </remarks>
    [Fact]
    public void TopicFailureIsAnError()
    {
        var logger = new FakeLogger();
        var harness = new SubscriptionHarness(options: new SubscriptionOptions { Logger = logger });
        harness.Subscription.Add(Aaa, Bbb, "CCC US Equity");
        var ids = harness.Ids();
        harness.Subscription.Remove("CCC US Equity");

        harness.Deliver(Status(
            Started(ids[Aaa]),
            Failed(ids[Bbb], "bad"),
            Failed(ids["CCC US Equity"], "gone", "SubscriptionTerminated")));

        Assert.Equal(
            [(LogLevel.Error, $"Subscription SubscriptionFailure topic={Bbb} desc=bad")],
            logger.Collector.GetSnapshot().Select(record => (record.Level, record.Message)));
    }

    /// <summary>
    /// Connection, event-queue and session events each log their own line and level.
    /// </summary>
    /// <remarks>
    /// Mutation: a branch dropped, or connection down logged as information.
    /// Oracle: a hand-written level and line for each event type, in delivery order.
    /// </remarks>
    [Fact]
    public void SessionEventsLogAtTheirLevels()
    {
        var logger = new FakeLogger();
        var harness = new SubscriptionHarness(options: new SubscriptionOptions { Logger = logger });

        harness.Deliver(
            Session("SessionConnectionDown"),
            Session("SessionConnectionUp"),
            RefDataEvents.Admin(Event.EventType.ADMIN, "<SlowConsumerWarning/>"),
            RefDataEvents.Admin(Event.EventType.ADMIN, "<SlowConsumerWarningCleared/>"),
            RefDataEvents.Admin(Event.EventType.ADMIN, "<DataLoss/>"),
            Session("SessionTerminated"));

        // The data-loss line ends with the printed message, cut at its colon.
        Assert.Equal(
            [
                (LogLevel.Warning, "Bloomberg connection down"),
                (LogLevel.Information, "Bloomberg connection up"),
                (LogLevel.Warning, "Event queue nearly full; ticks may be dropped"),
                (LogLevel.Warning, "Event queue no longer nearly full"),
                (LogLevel.Warning, "Event queue overflowed, data lost"),
                (LogLevel.Information, "Bloomberg session terminated"),
            ],
            logger.Collector.GetSnapshot()
                .Select(record => (record.Level, record.Message.Split(':')[0])));
    }

    /// <summary>
    /// The first callback error is an error with its exception, and each later one a
    /// debug line with its type and message.
    /// </summary>
    /// <remarks>
    /// Mutation: every error logged as an error, or the first logged without its
    /// exception.
    /// Oracle: a callback that throws on each of two ticks.
    /// </remarks>
    [Fact]
    public void OnlyTheFirstCallbackErrorCarriesItsException()
    {
        var logger = new FakeLogger();
        var harness = new SubscriptionHarness(
            (_, _) => throw new InvalidOperationException("boom"),
            options: new SubscriptionOptions { Logger = logger });
        harness.Subscription.Add(Aaa);
        var id = harness.Ids()[Aaa];

        harness.Deliver(
            Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")),
            Data((id, "<LAST_PRICE>2.0</LAST_PRICE>")));

        string laterLine = $"Subscription error on {Aaa}: InvalidOperationException: boom";
        Assert.Equal(
            [
                (LogLevel.Error, $"Subscription error on {Aaa}", "boom"),
                (LogLevel.Debug, laterLine, null),
            ],
            logger.Collector.GetSnapshot()
                .Select(record => (record.Level, record.Message, record.Exception?.Message)));
    }

    /// <summary>A Stop that times out logs a warning naming the wait.</summary>
    /// <remarks>
    /// Mutation: the timeout returned without a log line.
    /// Oracle: BeginStop delivers nothing, so a zero wait times out.
    /// </remarks>
    [Fact]
    public void StopTimeoutIsAWarning()
    {
        var logger = new FakeLogger();
        var harness = new SubscriptionHarness(options: new SubscriptionOptions { Logger = logger });

        harness.Subscription.Stop(TimeSpan.Zero);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(
            (LogLevel.Warning, "Bloomberg session did not stop within 00:00:00"),
            (record.Level, record.Message));
    }
}
