using Bloomberglp.Blpapi;
using Blp.IntradayBars;

namespace Blp.Tests.IntradayBars;

public class IntradayBarHandlerTests
{
    private static readonly IntradayBarRequest _request = new(
        "IBM US Equity",
        new DateTimeOffset(2024, 3, 15, 9, 30, 0, TimeSpan.FromHours(-4)),
        new DateTimeOffset(2024, 3, 15, 16, 0, 0, TimeSpan.FromHours(-4)));

    /// <summary>
    /// CreateRequest sends the event by Bloomberg name, the range in UTC and the
    /// interval.
    /// </summary>
    /// <remarks>
    /// Mutation: the event sent by its C# name (BestBid), the range sent as its local
    /// wall clock, or the interval left out.
    /// Oracle: 09:30 at -04:00 is 13:30 UTC, worked by hand; the Bloomberg event
    /// names.
    /// </remarks>
    [Fact]
    public void CreateRequestSendsEventRangeAndInterval()
    {
        var request = _request with { Event = IntradayEventType.BestBid, Interval = 5 };

        var sent = new IntradayBarHandler(request).CreateRequest(RefDataEvents.Service);

        Assert.Equal("BEST_BID", sent.GetElement(Name.GetName("eventType")).GetValueAsString());
        var start = sent.GetElement(Name.GetName("startDateTime")).GetValueAsDatetime();
        var end = sent.GetElement(Name.GetName("endDateTime")).GetValueAsDatetime();
        Assert.Equal(new DateTime(2024, 3, 15, 13, 30, 0), start.ToSystemDateTime());
        Assert.Equal(new DateTime(2024, 3, 15, 20, 0, 0), end.ToSystemDateTime());
        Assert.Equal(5, sent.GetElement(Name.GetName("interval")).GetValueAsInt32());
    }

    /// <summary>Bars from every partial response come back in order.</summary>
    /// <remarks>
    /// Mutation: a partial response's bars dropped, or bars read from tickData.
    /// Oracle: the hand-written response.
    /// </remarks>
    [Fact]
    public void ResponseGivesBarsInOrder()
    {
        var handler = new IntradayBarHandler(_request);

        handler.ProcessResponse(BarResponse(Event.EventType.PARTIAL_RESPONSE, "160.5", "1000"));
        handler.ProcessResponse(BarResponse(Event.EventType.RESPONSE, "161.0", "2000"));
        var bars = handler.ToResponse();

        Assert.Equal([160.5, 161.0], bars.Select(bar => bar["open"]));
        Assert.Equal([1000L, 2000L], bars.Select(bar => bar["volume"]));
    }

    private static Event BarResponse(Event.EventType eventType, string open, string volume) =>
        RefDataEvents.Response(
            "IntradayBarRequest",
            eventType,
            $"""
            <Response>
              <barData>
                <barTickData>
                  <time>2024-03-15T13:30:00.000+00:00</time>
                  <open>{open}</open><high>{open}</high><low>{open}</low><close>{open}</close>
                  <volume>{volume}</volume><numEvents>3</numEvents>
                </barTickData>
              </barData>
            </Response>
            """);
}
