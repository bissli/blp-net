using Blp.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Blp.Tests.Subscriptions;

/// <summary>The lines a <see cref="CallbackLogger"/> hands its callback.</summary>
public class CallbackLoggerTests
{
    /// <summary>
    /// A subscription's line reaches the callback with its level, its formatted text
    /// and its exception.
    /// </summary>
    /// <remarks>
    /// Mutation: the template passed in place of the formatted line, the exception
    /// dropped, or the level fixed.
    /// Oracle: a tick callback that throws, which the subscription logs as the error
    /// "Subscription error on AAA US Equity" carrying that exception.
    /// </remarks>
    [Fact]
    public void LineReachesTheCallbackFormatted()
    {
        List<(LogLevel Level, string Line, Exception? Error)> lines = [];
        var logger = new CallbackLogger((level, line, error) => lines.Add((level, line, error)));
        var boom = new InvalidOperationException("boom");
        var harness = new SubscriptionHarness(
            onTick: (_, _) => throw boom,
            options: new SubscriptionOptions { Logger = logger });
        harness.Subscription.Add("AAA US Equity");

        var id = harness.Ids()["AAA US Equity"];

        harness.Deliver(MarketDataEvents.Data((id, "<LAST_PRICE>1.0</LAST_PRICE>")));

        Assert.Equal(
            [(LogLevel.Error, "Subscription error on AAA US Equity", boom)],
            lines);
    }

    /// <summary>
    /// A line reaches the callback at its own level, and a line at
    /// <see cref="LogLevel.None"/> never reaches it.
    /// </summary>
    /// <remarks>
    /// Mutation: the level fixed, such as every line sent as Error, or the IsEnabled
    /// check dropped from Log.
    /// Oracle: one Warning line and one None line, written directly.
    /// </remarks>
    [Fact]
    public void LevelPassesThroughAndNoneIsDropped()
    {
        List<LogLevel> levels = [];
        var logger = new CallbackLogger((level, _, _) => levels.Add(level));

        logger.Log(LogLevel.Warning, default, "kept", null, (state, _) => state);
        logger.Log(LogLevel.None, default, "skipped", null, (state, _) => state);

        Assert.Equal([LogLevel.Warning], levels);
        Assert.False(logger.IsEnabled(LogLevel.None));
    }
}
