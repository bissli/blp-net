using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.IntradayTicks;

/// <summary>
/// Sends one <see cref="IntradayTickRequest"/> and collects its ticks.
/// </summary>
/// <param name="request">The request to send.</param>
internal sealed class IntradayTickHandler(IntradayTickRequest request) : IRequestHandler
{
    private readonly IntradayTickRequest _request = request;
    private readonly ElementParser _parser = new(request.TimeZone, false, false);
    private readonly List<IReadOnlyDictionary<string, object?>> _ticks = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("IntradayTickRequest");
        bloombergRequest.Set(ElementNames.Security, _request.Ticker);
        foreach (var eventType in _request.Events)
        {
            bloombergRequest.Append(ElementNames.EventTypes, ElementNames.BloombergName(eventType));
        }

        bloombergRequest.Set(ElementNames.StartDateTime, new Datetime(_request.Start.UtcDateTime));
        bloombergRequest.Set(ElementNames.EndDateTime, new Datetime(_request.End.UtcDateTime));
        (bool Include, Name Element)[] flags =
        [
            (_request.IncludeActionCodes, ElementNames.IncludeActionCodes),
            (_request.IncludeBicMicCodes, ElementNames.IncludeBicMicCodes),
            (_request.IncludeBrokerCodes, ElementNames.IncludeBrokerCodes),
            (_request.IncludeConditionCodes, ElementNames.IncludeConditionCodes),
            (_request.IncludeEquityRefPrice, ElementNames.IncludeEqRefPrice),
            (_request.IncludeIndicatorCodes, ElementNames.IncludeIndicatorCodes),
            (_request.IncludeRpsCodes, ElementNames.IncludeRpsCodes),
            (_request.IncludeTradeDate, ElementNames.IncludeTradeDate),
            (_request.IncludeTradeTime, ElementNames.IncludeTradeTime),
        ];
        foreach (var (include, element) in flags)
        {
            if (include) bloombergRequest.Set(element, true);
        }

        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            var tickData = message.GetElement(ElementNames.TickData);
            if (!tickData.HasElement(ElementNames.TickData)) continue;
            var ticks = tickData.GetElement(ElementNames.TickData);
            for (int index = 0; index < ticks.NumValues; index++)
            {
                _ticks.Add(_parser.RowValues(ticks.GetValueAsElement(index)));
            }
        }
    }

    /// <summary>The ticks of every event processed so far, in time order.</summary>
    /// <returns>One name-to-value row per tick, with every name a tick holds.</returns>
    public List<IReadOnlyDictionary<string, object?>> ToResponse() =>
        ElementParser.WithEveryName(_ticks);
}
