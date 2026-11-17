using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.IntradayBars;

/// <summary>
/// Sends one <see cref="IntradayBarRequest"/> and collects its bars.
/// </summary>
/// <param name="request">The request to send.</param>
internal sealed class IntradayBarHandler(IntradayBarRequest request) : IRequestHandler
{
    private readonly IntradayBarRequest _request = request;
    private readonly ElementParser _parser = new(request.TimeZone, false, false);
    private readonly List<IReadOnlyDictionary<string, object?>> _bars = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("IntradayBarRequest");
        bloombergRequest.Set(ElementNames.Security, _request.Ticker);
        bloombergRequest.Set(ElementNames.EventType, ElementNames.BloombergName(_request.Event));
        bloombergRequest.Set(ElementNames.StartDateTime, new Datetime(_request.Start.UtcDateTime));
        bloombergRequest.Set(ElementNames.EndDateTime, new Datetime(_request.End.UtcDateTime));
        bloombergRequest.Set(ElementNames.Interval, _request.Interval);
        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            var barData = message.GetElement(ElementNames.BarData);
            if (!barData.HasElement(ElementNames.BarTickData)) continue;
            var bars = barData.GetElement(ElementNames.BarTickData);
            for (int index = 0; index < bars.NumValues; index++)
            {
                _bars.Add(_parser.RowValues(bars.GetValueAsElement(index)));
            }
        }
    }

    /// <summary>The bars of every event processed so far, in time order.</summary>
    /// <returns>One name-to-value row per bar, with every name any bar holds.</returns>
    public List<IReadOnlyDictionary<string, object?>> ToResponse() =>
        ElementParser.WithEveryName(_bars);
}
