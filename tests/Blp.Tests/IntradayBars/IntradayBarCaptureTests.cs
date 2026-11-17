using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.IntradayBars;

namespace Blp.Tests.IntradayBars;

/// <summary>
/// <see cref="IntradayBarHandler"/> over excerpts of responses a terminal sent, on the
/// real service shape.
/// </summary>
public class IntradayBarCaptureTests
{
    private static readonly TimeSpan _minusFive = TimeSpan.FromHours(-5);

    /// <summary>
    /// CreateRequest sends the window in UTC and the interval it was given, for trades.
    /// </summary>
    /// <remarks>
    /// Mutation: the window sent as its local wall clock, or the interval left at its
    /// default.
    /// Oracle: the printed request of the intraday_bars capture. The window is 09:30 to
    /// 16:00 at -05:00, which is 14:30 to 21:00 UTC.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedFiveMinuteRequest()
    {
        var handler = new IntradayBarHandler(
            new IntradayBarRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 9, 30, 0, _minusFive),
                new DateTimeOffset(2026, 11, 16, 16, 0, 0, _minusFive))
            {
                Interval = 5,
            });

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            IntradayBarRequest = {
                security = "IBM US Equity"
                eventType = TRADE
                startDateTime = 2026-11-16T14:30:00.000+00:00
                endDateTime = 2026-11-16T21:00:00.000+00:00
                interval = 5
            }
            """,
            printed);
    }

    /// <summary>With no options CreateRequest asks for one-minute trade bars.</summary>
    /// <remarks>
    /// Mutation: another default interval or event type.
    /// Oracle: the printed request of the intraday_bars_default_interval capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedDefaultRequest()
    {
        var handler = new IntradayBarHandler(
            new IntradayBarRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero)));

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            IntradayBarRequest = {
                security = "IBM US Equity"
                eventType = TRADE
                startDateTime = 2026-11-16T14:30:00.000+00:00
                endDateTime = 2026-11-16T15:00:00.000+00:00
                interval = 1
            }
            """,
            printed);
    }

    /// <summary>
    /// A bar is a row of its time in the request's zone and every column Bloomberg
    /// sent, with the volume an integer and the other columns floats.
    /// </summary>
    /// <remarks>
    /// Mutation: the time left at UTC, the volume read as a float, the value column
    /// Bloomberg adds dropped, or the response-level delayedSecurity flag added to a
    /// row.
    /// Oracle: the first two bars of the intraday_bars capture. 14:30Z at -05:00, in
    /// force on 2026-11-16, is 09:30, worked by hand.
    /// </remarks>
    [Fact]
    public void BarsKeepEveryColumnTyped()
    {
        var handler = new IntradayBarHandler(
            new IntradayBarRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero))
            {
                Interval = 5,
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
            });

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "IntradayBarRequest",
            Event.EventType.RESPONSE,
            """
            <IntradayBarResponse>
              <barData>
                <barTickData>
                  <time>2026-11-16T14:30:00.000</time>
                  <open>246.95</open><high>247.07</high><low>245.645</low>
                  <close>246.5432</close><volume>57295</volume><numEvents>124</numEvents>
                  <value>14139090</value>
                </barTickData>
                <barTickData>
                  <time>2026-11-16T14:35:00.000</time>
                  <open>246.26</open><high>246.5192</high><low>244.73</low>
                  <close>245.9661</close><volume>26104</volume><numEvents>159</numEvents>
                  <value>6408097.5</value>
                </barTickData>
                <delayedSecurity>true</delayedSecurity>
              </barData>
            </IntradayBarResponse>
            """));

        Assert.Equal(
            [
                Bar(
                    "2026-11-16T09:30:00-05:00",
                    246.95, 247.07, 245.645, 246.5432, 57295L, 124, 14139090.0),
                Bar(
                    "2026-11-16T09:35:00-05:00",
                    246.26, 246.5192, 244.73, 245.9661, 26104L, 159, 6408097.5),
            ],
            handler.ToResponse().Select(WithWallClock));
    }

    /// <summary>
    /// A copy of row whose time is its (wall clock, offset) pair, since a
    /// <see cref="DateTimeOffset"/> compares by instant alone.
    /// </summary>
    private static Dictionary<string, object?> WithWallClock(
        IReadOnlyDictionary<string, object?> row)
    {
        var time = Assert.IsType<DateTimeOffset>(row["time"]);
        return new(row) { ["time"] = (time.DateTime, time.Offset) };
    }

    private static Dictionary<string, object?> Bar(
        string time,
        double open,
        double high,
        double low,
        double close,
        long volume,
        int numEvents,
        double value)
    {
        var parsed = DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
        return new()
        {
            ["time"] = (parsed.DateTime, parsed.Offset),
            ["open"] = open,
            ["high"] = high,
            ["low"] = low,
            ["close"] = close,
            ["volume"] = volume,
            ["numEvents"] = numEvents,
            ["value"] = value,
        };
    }
}
