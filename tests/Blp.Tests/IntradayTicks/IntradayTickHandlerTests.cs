using Bloomberglp.Blpapi;
using Blp.Internal;
using Blp.IntradayTicks;

namespace Blp.Tests.IntradayTicks;

public class IntradayTickHandlerTests
{
    private static readonly TimeSpan _plusFive = TimeSpan.FromHours(5);

    private static readonly IntradayTickRequest _request = new(
        "IBM US Equity",
        new DateTimeOffset(2024, 3, 15, 14, 30, 0, _plusFive),
        new DateTimeOffset(2024, 3, 15, 16, 0, 0, _plusFive))
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"),
    };

    /// <summary>
    /// CreateRequest sends the range in UTC and the events by Bloomberg name.
    /// </summary>
    /// <remarks>
    /// Mutation: the range sent as its local wall clock, or an event sent by its C#
    /// name (BidBest).
    /// Oracle: 14:30 at +05:00 is 09:30 UTC, worked by hand; the Bloomberg event
    /// names.
    /// </remarks>
    [Fact]
    public void CreateRequestSendsUtcRangeAndEventNames()
    {
        var request = _request with { Events = [IntradayEventType.Bid, IntradayEventType.BidBest] };

        var sent = new IntradayTickHandler(request).CreateRequest(RefDataEvents.Service);

        Assert.Equal("IBM US Equity", Text(sent, "security"));
        var events = sent.GetElement(Name.GetName("eventTypes"));
        Assert.Equal(["BID", "BID_BEST"], [events.GetValueAsString(0), events.GetValueAsString(1)]);
        var start = sent.GetElement(Name.GetName("startDateTime")).GetValueAsDatetime();
        var end = sent.GetElement(Name.GetName("endDateTime")).GetValueAsDatetime();
        Assert.Equal(new DateTime(2024, 3, 15, 9, 30, 0), start.ToSystemDateTime());
        Assert.Equal(new DateTime(2024, 3, 15, 11, 0, 0), end.ToSystemDateTime());
    }

    /// <summary>Every event type maps to a Bloomberg enumerator.</summary>
    /// <remarks>
    /// Mutation: a member whose name maps to no enumerator, such as BidYields.
    /// Oracle: the EventType enumerators of the //blp/refdata schema, in member order.
    /// </remarks>
    [Fact]
    public void EveryEventTypeNamesASchemaEnumerator() =>
        Assert.Equal(
            [
                "TRADE", "BID", "ASK", "BID_BEST", "ASK_BEST", "MID_PRICE", "AT_TRADE",
                "BEST_BID", "BEST_ASK", "BID_YIELD", "ASK_YIELD", "SETTLE",
            ],
            Enum.GetValues<IntradayEventType>()
                .Select(eventType => ElementNames.BloombergName(eventType)));

    /// <summary>
    /// Each include flag set sends its Bloomberg element as true; an unset one is
    /// not sent.
    /// </summary>
    /// <remarks>
    /// Mutation: a flag sent under its property name instead of its element name
    /// (includeEquityRefPrice for includeEqRefPrice, includeRspCodes for
    /// includeRpsCodes), or an unset flag sent as false.
    /// Oracle: the documented IntradayTickRequest element names.
    /// </remarks>
    [Fact]
    public void CreateRequestSendsSetFlagsOnly()
    {
        var request = _request with
        {
            IncludeConditionCodes = true,
            IncludeEquityRefPrice = true,
            IncludeRpsCodes = true,
        };

        var sent = new IntradayTickHandler(request).CreateRequest(RefDataEvents.Service);

        foreach (string name in new[]
        {
            "includeConditionCodes", "includeEqRefPrice", "includeRpsCodes",
        })
        {
            Assert.True(sent.GetElement(Name.GetName(name)).GetValueAsBool(), name);
        }

        foreach (string name in new[]
        {
            "includeActionCodes", "includeBicMicCodes", "includeBrokerCodes",
            "includeIndicatorCodes", "includeTradeDate", "includeTradeTime",
        })
        {
            Assert.False(sent.HasElement(Name.GetName(name)), name);
        }
    }

    /// <summary>
    /// Ticks from every partial response come back in order, each with every name
    /// any tick holds.
    /// </summary>
    /// <remarks>
    /// Mutation: a partial response's ticks dropped, the time left in UTC or put in the
    /// host zone, or a tick without conditionCodes missing the key.
    /// Oracle: the hand-written response; 14:30 UTC is 23:30 JST (+09:00), worked by
    /// hand.
    /// </remarks>
    [Fact]
    public void ResponseGivesTicksInOrder()
    {
        var handler = new IntradayTickHandler(_request);

        handler.ProcessResponse(TickResponse(
            Event.EventType.PARTIAL_RESPONSE,
            """
            <tickData>
              <time>2024-03-15T14:30:00.000+00:00</time>
              <type>BID</type><value>160.5</value><size>100</size>
            </tickData>
            """));
        handler.ProcessResponse(TickResponse(
            Event.EventType.RESPONSE,
            """
            <tickData>
              <time>2024-03-15T14:31:00.000+00:00</time>
              <type>ASK</type><value>160.75</value><size>200</size>
              <conditionCodes>R6</conditionCodes>
            </tickData>
            """));
        var ticks = handler.ToResponse();

        Assert.Equal(2, ticks.Count);
        var time = Assert.IsType<DateTimeOffset>(ticks[0]["time"]);
        Assert.Equal(
            (new DateTime(2024, 3, 15, 23, 30, 0), TimeSpan.FromHours(9)),
            (time.DateTime, time.Offset));
        Assert.Equal("BID", ticks[0]["type"]);
        Assert.Equal(160.5, ticks[0]["value"]);
        Assert.Null(ticks[0]["conditionCodes"]);
        Assert.Equal("ASK", ticks[1]["type"]);
        Assert.Equal("R6", ticks[1]["conditionCodes"]);
    }

    private static string Text(Request request, string name) =>
        request.GetElement(Name.GetName(name)).GetValueAsString();

    private static Event TickResponse(Event.EventType eventType, string ticksXml) =>
        RefDataEvents.Response(
            "IntradayTickRequest",
            eventType,
            $"<Response><tickData>{ticksXml}</tickData></Response>");
}
