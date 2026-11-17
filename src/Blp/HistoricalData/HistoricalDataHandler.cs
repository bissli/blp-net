using System.Globalization;
using System.Text.Json;
using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.HistoricalData;

/// <summary>
/// Sends one <see cref="HistoricalDataRequest"/> and collects its response.
/// </summary>
/// <param name="request">The request to send.</param>
internal sealed class HistoricalDataHandler(HistoricalDataRequest request) : IRequestHandler
{
    private readonly HistoricalDataRequest _request = request;
    private readonly ElementParser _parser = new(request.TimeZone, false, false);
    private readonly Dictionary<string, IReadOnlyList<HistoricalDataRow>> _rowsBySecurity = [];
    private readonly List<SecurityError> _securityErrors = [];
    private readonly List<FieldError> _fieldErrors = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("HistoricalDataRequest");
        foreach (string ticker in _request.Tickers)
        {
            bloombergRequest.Append(ElementNames.Securities, ticker);
        }

        foreach (string field in _request.Fields)
        {
            bloombergRequest.Append(ElementNames.Fields, field);
        }

        bloombergRequest.Set(ElementNames.StartDate, BloombergDate(_request.Start));
        bloombergRequest.Set(ElementNames.EndDate, BloombergDate(_request.End));
        bloombergRequest.Set(ElementNames.PeriodicitySelection, BloombergName(_request.Period));
        if (_request.PeriodAdjustment is { } adjustment)
        {
            bloombergRequest.Set(ElementNames.PeriodicityAdjustment, BloombergName(adjustment));
        }

        if (_request.Currency is { } currency)
        {
            bloombergRequest.Set(ElementNames.Currency, currency);
        }

        if (_request.OverrideOption is { } overrideOption)
        {
            bloombergRequest.Set(
                ElementNames.OverrideOption, "OVERRIDE_OPTION_" + BloombergName(overrideOption));
        }

        if (_request.PricingOption is { } pricingOption)
        {
            bloombergRequest.Set(
                ElementNames.PricingOption, "PRICING_OPTION_" + BloombergName(pricingOption));
        }

        if (_request.NonTradingDayFillOption is { } fillOption)
        {
            bloombergRequest.Set(ElementNames.NonTradingDayFillOption, BloombergName(fillOption));
        }

        if (_request.NonTradingDayFillMethod is { } fillMethod)
        {
            bloombergRequest.Set(ElementNames.NonTradingDayFillMethod, BloombergName(fillMethod));
        }

        if (_request.MaxDataPoints is { } maxDataPoints)
        {
            bloombergRequest.Set(ElementNames.MaxDataPoints, maxDataPoints);
        }

        FieldOverrides.AppendTo(bloombergRequest, _request.Overrides);
        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            var securityData = message.GetElement(ElementNames.SecurityData);
            if (ElementParser.SecurityErrorOf(securityData) is { } error)
            {
                _securityErrors.Add(error);
                continue;
            }

            var points = securityData.GetElement(ElementNames.FieldData);
            List<HistoricalDataRow> rows = [];
            for (int index = 0; index < points.NumValues; index++)
            {
                var point = points.GetValueAsElement(index);
                Dictionary<string, object?> values = [];
                foreach (string field in _request.Fields)
                {
                    values[field] = _parser.SubelementValue(point, Name.GetName(field));
                }

                var date = ElementParser.ToDateOnly(point.GetElementAsDate(ElementNames.Date));
                rows.Add(new HistoricalDataRow(date, values));
            }

            _rowsBySecurity[securityData.GetElementAsString(ElementNames.Security)] = rows;
            _fieldErrors.AddRange(ElementParser.FieldErrorsOf(securityData));
        }
    }

    /// <summary>The response built from every event processed so far.</summary>
    /// <returns>The response.</returns>
    public HistoricalDataResponse ToResponse() =>
        new(_rowsBySecurity, _securityErrors, _fieldErrors);

    /// <summary>
    /// The Bloomberg enumeration value of an option, such as SEMI_ANNUALLY.
    /// </summary>
    /// <param name="option">An option enum value.</param>
    /// <returns>The value's name in upper snake case.</returns>
    internal static string BloombergName(Enum option) =>
        JsonNamingPolicy.SnakeCaseUpper.ConvertName(option.ToString());

    private static string BloombergDate(DateOnly date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
