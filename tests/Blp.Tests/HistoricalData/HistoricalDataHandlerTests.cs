using Bloomberglp.Blpapi;
using Blp.HistoricalData;

namespace Blp.Tests.HistoricalData;

public class HistoricalDataHandlerTests
{
    private static readonly HistoricalDataRequest _request = new(
        ["IBM US Equity"],
        ["PX_LAST", "VOLUME"],
        new DateOnly(2024, 1, 2),
        new DateOnly(2024, 3, 29))
    {
        TimeZone = TimeZoneInfo.Utc,
    };

    /// <summary>
    /// CreateRequest sends every option under its Bloomberg enumeration name.
    /// </summary>
    /// <remarks>
    /// Mutation: an enum sent by its C# name (SemiAnnually), a prefix dropped from
    /// OVERRIDE_OPTION_ or PRICING_OPTION_, or a date in ISO form.
    /// Oracle: the //blp/refdata HistoricalDataRequest enumeration values.
    /// </remarks>
    [Fact]
    public void CreateRequestNamesEveryOption()
    {
        var request = _request with
        {
            Period = Periodicity.SemiAnnually,
            PeriodAdjustment = PeriodicityAdjustment.Calendar,
            Currency = "EUR",
            OverrideOption = OverrideOption.Gpa,
            PricingOption = PricingOption.Yield,
            NonTradingDayFillOption = NonTradingDayFillOption.AllCalendarDays,
            NonTradingDayFillMethod = NonTradingDayFillMethod.NilValue,
            MaxDataPoints = 5,
            Overrides = new Dictionary<string, string> { ["BEST_FPERIOD_OVERRIDE"] = "1BF" },
        };

        var sent = new HistoricalDataHandler(request).CreateRequest(RefDataEvents.Service);

        Assert.Equal("20240102", Text(sent, "startDate"));
        Assert.Equal("20240329", Text(sent, "endDate"));
        Assert.Equal("SEMI_ANNUALLY", Text(sent, "periodicitySelection"));
        Assert.Equal("CALENDAR", Text(sent, "periodicityAdjustment"));
        Assert.Equal("EUR", Text(sent, "currency"));
        Assert.Equal("OVERRIDE_OPTION_GPA", Text(sent, "overrideOption"));
        Assert.Equal("PRICING_OPTION_YIELD", Text(sent, "pricingOption"));
        Assert.Equal("ALL_CALENDAR_DAYS", Text(sent, "nonTradingDayFillOption"));
        Assert.Equal("NIL_VALUE", Text(sent, "nonTradingDayFillMethod"));
        Assert.Equal(5, sent.GetElement(Name.GetName("maxDataPoints")).GetValueAsInt32());
        var entry = sent.GetElement(Name.GetName("overrides")).GetValueAsElement(0);
        Assert.Equal("1BF", entry.GetElementAsString(Name.GetName("value")));
    }

    /// <summary>
    /// An option left null is not sent, so Bloomberg's default holds.
    /// </summary>
    /// <remarks>
    /// Mutation: a null option sent as its enum's first member, or maxDataPoints sent
    /// as 0.
    /// Oracle: the request schema, where each option is optional.
    /// </remarks>
    [Fact]
    public void CreateRequestOmitsNullOptions()
    {
        var sent = new HistoricalDataHandler(_request).CreateRequest(RefDataEvents.Service);

        Assert.Equal("DAILY", Text(sent, "periodicitySelection"));
        foreach (string name in new[]
        {
            "periodicityAdjustment", "currency", "overrideOption", "pricingOption",
            "nonTradingDayFillOption", "nonTradingDayFillMethod", "maxDataPoints",
        })
        {
            Assert.False(sent.HasElement(Name.GetName(name)), name);
        }
    }

    /// <summary>
    /// Each message gives one security's points in order, or its security error.
    /// </summary>
    /// <remarks>
    /// Mutation: points out of order, a field Bloomberg did not send left out, the
    /// date read from the wrong element, or a rejected security given an empty list.
    /// Oracle: the hand-written response.
    /// </remarks>
    [Fact]
    public void ResponseGivesRowsAndErrors()
    {
        var handler = new HistoricalDataHandler(_request);

        handler.ProcessResponse(HistoricalResponse(
            Event.EventType.PARTIAL_RESPONSE,
            """
            <securityData>
              <security>IBM US Equity</security>
              <fieldExceptions>
                <fieldId>VOLUME</fieldId>
                <errorInfo><category>BAD_FLD</category></errorInfo>
              </fieldExceptions>
              <fieldData><date>2024-01-02</date><PX_LAST>160.5</PX_LAST></fieldData>
              <fieldData><date>2024-01-03</date><PX_LAST>161.0</PX_LAST></fieldData>
            </securityData>
            """));
        handler.ProcessResponse(HistoricalResponse(
            Event.EventType.RESPONSE,
            """
            <securityData>
              <security>BAD US Equity</security>
              <securityError><category>BAD_SEC</category></securityError>
            </securityData>
            """));
        var response = handler.ToResponse();

        var (security, rows) = Assert.Single(response.Rows);
        Assert.Equal("IBM US Equity", security);
        Assert.Equal(
            [new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3)], rows.Select(row => row.Date));
        Assert.Equal([160.5, 161.0], rows.Select(row => row.Values["PX_LAST"]));
        Assert.All(rows, row => Assert.Null(row.Values["VOLUME"]));
        Assert.Equal("BAD US Equity", Assert.Single(response.SecurityErrors).Security);
        Assert.Equal("VOLUME", Assert.Single(response.FieldErrors).Field);
    }

    /// <summary>A historical field error is kept in full, beside the values.</summary>
    /// <remarks>
    /// Mutation: the field errors of a historical securityData element dropped, or the
    /// message and subcategory left out of the record.
    /// Oracle: the Python blp test_historical_field_error_recorded expected FieldError
    /// and PX_LAST value.
    /// </remarks>
    [Fact]
    public void FieldErrorIsRecorded()
    {
        var handler = new HistoricalDataHandler(
            _request with { Fields = ["PX_LAST", "BAD_FIELD"] });

        handler.ProcessResponse(HistoricalResponse(
            Event.EventType.RESPONSE,
            """
            <securityData>
              <security>IBM US Equity</security>
              <fieldExceptions>
                <fieldId>BAD_FIELD</fieldId>
                <errorInfo>
                  <category>BAD_FLD</category>
                  <message>Field not valid</message>
                  <subcategory>INVALID_FIELD</subcategory>
                </errorInfo>
              </fieldExceptions>
              <fieldData><date>2024-01-02</date><PX_LAST>161.5</PX_LAST></fieldData>
            </securityData>
            """));
        var response = handler.ToResponse();

        Assert.Equal(
            new FieldError(
                "IBM US Equity", "BAD_FIELD", "BAD_FLD", "Field not valid", "INVALID_FIELD"),
            Assert.Single(response.FieldErrors));
        var row = Assert.Single(response.Rows["IBM US Equity"]);
        Assert.Equal(161.5, row.Values["PX_LAST"]);
    }

    /// <summary>A date sends the calendar date it holds, for start and end.</summary>
    /// <remarks>
    /// Mutation: the start or end date sent a day off, as a conversion through a
    /// DateTime at midnight in another zone would.
    /// Oracle: the Python blp test_historical_date_in_own_timezone expected value for
    /// its date(2024, 1, 2) input. The string and datetime inputs have no C# form.
    /// </remarks>
    [Fact]
    public void DateIsSentAsItsOwnCalendarDate()
    {
        var day = new DateOnly(2024, 1, 2);

        var sent = new HistoricalDataHandler(_request with { Start = day, End = day })
            .CreateRequest(RefDataEvents.Service);

        Assert.Equal("20240102", Text(sent, "startDate"));
        Assert.Equal("20240102", Text(sent, "endDate"));
    }

    private static string Text(Request request, string name) =>
        request.GetElement(Name.GetName(name)).GetValueAsString();

    private static Event HistoricalResponse(Event.EventType eventType, string securityDataXml) =>
        RefDataEvents.Response(
            "HistoricalDataRequest", eventType, $"<Response>{securityDataXml}</Response>");
}
