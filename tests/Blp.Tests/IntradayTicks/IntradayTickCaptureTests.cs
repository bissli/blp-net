using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.IntradayTicks;

namespace Blp.Tests.IntradayTicks;

/// <summary>
/// <see cref="IntradayTickHandler"/> over excerpts of responses a terminal sent, on the
/// real service shape.
/// </summary>
public class IntradayTickCaptureTests
{
    private static readonly TimeSpan _minusFive = TimeSpan.FromHours(-5);

    private static readonly TimeZoneInfo _newYork =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// CreateRequest sends the window in UTC, the events in the order given, and every
    /// include flag set.
    /// </summary>
    /// <remarks>
    /// Mutation: the window sent as its local wall clock, the event order changed, or a
    /// flag left unset.
    /// Oracle: the printed request of the intraday_ticks capture. The window is
    /// 09:30 to 09:40 at -05:00, which is 14:30 to 14:40 UTC.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedFlagRequest()
    {
        var handler = new IntradayTickHandler(
            new IntradayTickRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 9, 30, 0, _minusFive),
                new DateTimeOffset(2026, 11, 16, 9, 40, 0, _minusFive))
            {
                Events = [IntradayEventType.Bid, IntradayEventType.Ask, IntradayEventType.Trade],
                IncludeConditionCodes = true,
                IncludeBrokerCodes = true,
                IncludeRpsCodes = true,
                IncludeBicMicCodes = true,
                IncludeActionCodes = true,
                IncludeIndicatorCodes = true,
                IncludeTradeTime = true,
                IncludeEquityRefPrice = true,
                IncludeTradeDate = true,
            });

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            IntradayTickRequest = {
                security = "IBM US Equity"
                startDateTime = 2026-11-16T14:30:00.000+00:00
                endDateTime = 2026-11-16T14:40:00.000+00:00
                eventTypes[] = {
                    BID, ASK, TRADE
                }
                includeConditionCodes = true
                includeBrokerCodes = true
                includeRpsCodes = true
                includeBicMicCodes = true
                includeActionCodes = true
                includeIndicatorCodes = true
                includeTradeTime = true
                includeEqRefPrice = true
                includeTradeDate = true
                filters[] = {
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// With no options CreateRequest asks for trades, asks and bids, in that order, and
    /// sets no include flag.
    /// </summary>
    /// <remarks>
    /// Mutation: another default event order, or a flag sent as false.
    /// Oracle: the printed request of the intraday_ticks_plain capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedPlainRequest()
    {
        var handler = new IntradayTickHandler(
            new IntradayTickRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 14, 32, 0, TimeSpan.Zero)));

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            IntradayTickRequest = {
                security = "IBM US Equity"
                startDateTime = 2026-11-16T14:30:00.000+00:00
                endDateTime = 2026-11-16T14:32:00.000+00:00
                eventTypes[] = {
                    TRADE, ASK, BID
                }
                filters[] = {
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// A tick has conditionCodes only where Bloomberg sent them, and a tick without
    /// them gets null, so every row has the same columns. Times are in the request's
    /// zone, to the microsecond.
    /// </summary>
    /// <remarks>
    /// Mutation: the column left off a tick that lacks it, a time left at UTC, the
    /// microseconds cut to milliseconds, or the size read as a float.
    /// Oracle: ticks 0, 4, 6 and 17 of the intraday_ticks capture. 14:30:00.275135Z at
    /// -05:00, in force on 2026-11-16, is 09:30:00.275135, worked by hand.
    /// </remarks>
    [Fact]
    public void TicksWithoutConditionCodesGetNull()
    {
        var handler = new IntradayTickHandler(
            new IntradayTickRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 14, 40, 0, TimeSpan.Zero))
            {
                IncludeConditionCodes = true,
                TimeZone = _newYork,
            });

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "IntradayTickRequest",
            Event.EventType.RESPONSE,
            """
            <IntradayTickResponse>
              <tickData>
                <tickData>
                  <time>2026-11-16T14:30:00.275135</time><type>BID</type>
                  <value>246.74</value><size>100</size>
                </tickData>
                <tickData>
                  <time>2026-11-16T14:30:01.285745</time><type>TRADE</type>
                  <value>246.95</value><size>36790</size>
                  <conditionCodes>R6,OP</conditionCodes>
                </tickData>
                <tickData>
                  <time>2026-11-16T14:30:01.327245</time><type>ASK</type>
                  <value>247</value><size>300</size>
                </tickData>
                <tickData>
                  <time>2026-11-16T14:30:01.337937</time><type>TRADE</type>
                  <value>246.825</value><size>100</size>
                </tickData>
              </tickData>
            </IntradayTickResponse>
            """));

        Assert.Equal(
            [
                Tick("2026-11-16T09:30:00.275135-05:00", "BID", 246.74, 100, null),
                Tick("2026-11-16T09:30:01.285745-05:00", "TRADE", 246.95, 36790, "R6,OP"),
                Tick("2026-11-16T09:30:01.327245-05:00", "ASK", 247.0, 300, null),
                Tick("2026-11-16T09:30:01.337937-05:00", "TRADE", 246.825, 100, null),
            ],
            handler.ToResponse().Select(WithWallClock));
    }

    /// <summary>
    /// Ticks sent without any code request have only time, type, value and size.
    /// </summary>
    /// <remarks>
    /// Mutation: a conditionCodes column added with null, or the event type dropped.
    /// Oracle: ticks 0, 4 and 6 of the intraday_ticks_plain capture, read at UTC.
    /// </remarks>
    [Fact]
    public void PlainTicksHaveFourColumns()
    {
        var handler = new IntradayTickHandler(
            new IntradayTickRequest(
                "IBM US Equity",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 14, 32, 0, TimeSpan.Zero))
            {
                TimeZone = TimeZoneInfo.Utc,
            });

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "IntradayTickRequest",
            Event.EventType.RESPONSE,
            """
            <IntradayTickResponse>
              <tickData>
                <tickData>
                  <time>2026-11-16T14:30:00.275135</time><type>BID</type>
                  <value>246.74</value><size>100</size>
                </tickData>
                <tickData>
                  <time>2026-11-16T14:30:01.285745</time><type>TRADE</type>
                  <value>246.95</value><size>36790</size>
                </tickData>
                <tickData>
                  <time>2026-11-16T14:30:01.327245</time><type>ASK</type>
                  <value>247</value><size>300</size>
                </tickData>
              </tickData>
            </IntradayTickResponse>
            """));

        Assert.Equal(
            [
                Tick("2026-11-16T14:30:00.275135+00:00", "BID", 246.74, 100),
                Tick("2026-11-16T14:30:01.285745+00:00", "TRADE", 246.95, 36790),
                Tick("2026-11-16T14:30:01.327245+00:00", "ASK", 247.0, 300),
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

    private static Dictionary<string, object?> Tick(
        string time, string type, double value, int size)
    {
        var parsed = DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
        return new()
        {
            ["time"] = (parsed.DateTime, parsed.Offset),
            ["type"] = type,
            ["value"] = value,
            ["size"] = size,
        };
    }

    private static Dictionary<string, object?> Tick(
        string time, string type, double value, int size, string? conditionCodes)
    {
        var tick = Tick(time, type, value, size);
        tick["conditionCodes"] = conditionCodes;
        return tick;
    }
}
