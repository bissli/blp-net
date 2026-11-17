using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.HistoricalData;

/// <summary>
/// Sends one <see cref="HistoricalDataRequest"/> and collects its response.
/// </summary>
/// <param name="request">The request to send.</param>
/// <param name="timeProvider">
/// Clock for the local date of a null End. Null takes the system clock.
/// </param>
internal sealed class HistoricalDataHandler(
    HistoricalDataRequest request, TimeProvider? timeProvider = null) : IRequestHandler
{
    private readonly HistoricalDataRequest _request = request;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ElementParser _parser = new(request.TimeZone, false, false, timeProvider);
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

        var end = _request.End ?? DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var start = _request.Start ?? end.AddDays(-1);
        bloombergRequest.Set(ElementNames.StartDate, BloombergDate(start));
        bloombergRequest.Set(ElementNames.EndDate, BloombergDate(end));
        bloombergRequest.Set(
            ElementNames.PeriodicitySelection, ElementNames.BloombergName(_request.Period));
        if (_request.PeriodAdjustment is { } adjustment)
        {
            bloombergRequest.Set(
                ElementNames.PeriodicityAdjustment, ElementNames.BloombergName(adjustment));
        }

        if (_request.Currency is { } currency)
        {
            bloombergRequest.Set(ElementNames.Currency, currency);
        }

        if (_request.OverrideOption is { } overrideOption)
        {
            bloombergRequest.Set(
                ElementNames.OverrideOption,
                "OVERRIDE_OPTION_" + ElementNames.BloombergName(overrideOption));
        }

        if (_request.PricingOption is { } pricingOption)
        {
            bloombergRequest.Set(
                ElementNames.PricingOption,
                "PRICING_OPTION_" + ElementNames.BloombergName(pricingOption));
        }

        if (_request.NonTradingDayFillOption is { } fillOption)
        {
            bloombergRequest.Set(
                ElementNames.NonTradingDayFillOption, ElementNames.BloombergName(fillOption));
        }

        if (_request.NonTradingDayFillMethod is { } fillMethod)
        {
            bloombergRequest.Set(
                ElementNames.NonTradingDayFillMethod, ElementNames.BloombergName(fillMethod));
        }

        if (_request.MaxDataPoints is { } maxDataPoints)
        {
            bloombergRequest.Set(ElementNames.MaxDataPoints, maxDataPoints);
        }

        SetFlag(bloombergRequest, ElementNames.AdjustmentNormal, _request.AdjustmentNormal);
        SetFlag(bloombergRequest, ElementNames.AdjustmentAbnormal, _request.AdjustmentAbnormal);
        SetFlag(bloombergRequest, ElementNames.AdjustmentSplit, _request.AdjustmentSplit);
        SetFlag(
            bloombergRequest, ElementNames.AdjustmentFollowDpdf, _request.AdjustmentFollowDpdf);
        if (_request.CalendarCodeOverride is { } calendarCode)
        {
            bloombergRequest.Set(ElementNames.CalendarCodeOverride, calendarCode);
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
    /// <exception cref="RequestSecurityException">
    /// The request raises security errors and one was received.
    /// </exception>
    /// <exception cref="RequestFieldException">
    /// The request raises field errors and one was received.
    /// </exception>
    public HistoricalDataResponse ToResponse()
    {
        ErrorRaising.ThrowRequested(
            _request.RaiseSecurityError, _request.RaiseFieldError, _securityErrors, _fieldErrors);
        return new(_rowsBySecurity, _securityErrors, _fieldErrors);
    }

    private static void SetFlag(Request request, Name name, bool? value)
    {
        if (value is { } flag) request.Set(name, flag);
    }

    private static string BloombergDate(DateOnly date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
