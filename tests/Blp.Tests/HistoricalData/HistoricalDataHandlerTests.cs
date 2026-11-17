using Bloomberglp.Blpapi;
using Blp.HistoricalData;
using Blp.Internal;
using Microsoft.Extensions.Time.Testing;

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

    private const string FieldErrorXml = """
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
        """;

    private const string SecurityErrorXml = """
        <securityData>
          <security>BAD US Equity</security>
          <securityError>
            <category>BAD_SEC</category>
            <message>Unknown</message>
            <subcategory>INVALID_SECURITY</subcategory>
          </securityError>
        </securityData>
        """;

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

    /// <summary>Every fill method maps to a Bloomberg enumerator.</summary>
    /// <remarks>
    /// Mutation: a member spelled NextDayValue, which maps to NEXT_DAY_VALUE.
    /// Oracle: the NonTradingDayFillMethod enumerators of the //blp/refdata schema.
    /// </remarks>
    [Fact]
    public void EveryFillMethodNamesASchemaEnumerator() =>
        Assert.Equal(
            ["PREVIOUS_VALUE", "NIL_VALUE", "NEXTDAY_VALUE"],
            Enum.GetValues<NonTradingDayFillMethod>()
                .Select(method => ElementNames.BloombergName(method)));

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
    /// Oracle: the FieldError and the PX_LAST value that the response XML holds.
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
    /// Oracle: hand-worked: the date January 2, 2024 is the text 20240102.
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

    /// <summary>A missing start is the day before the end's own date.</summary>
    /// <remarks>
    /// Mutation: the start taken from the host's date, or left equal to the end.
    /// Oracle: hand-worked: the day before January 2, 2024 is 20240101.
    /// </remarks>
    [Fact]
    public void MissingStartIsTheDayBeforeEnd()
    {
        var request = new HistoricalDataRequest(
            ["IBM US Equity"], ["PX_LAST"], End: new DateOnly(2024, 1, 2));

        var sent = new HistoricalDataHandler(request, new FakeTimeProvider())
            .CreateRequest(RefDataEvents.Service);

        Assert.Equal("20240101", Text(sent, "startDate"));
        Assert.Equal("20240102", Text(sent, "endDate"));
    }

    /// <summary>A missing end is the host's local date.</summary>
    /// <remarks>
    /// Mutation: the end taken from the UTC date, which sends 20240102 and a start of
    /// 20240101 on a host east of UTC between local midnight and 00:00 UTC.
    /// Oracle: hand-worked: a Tokyo host at 08:00 on January 3 is 23:00 UTC on
    /// January 2, so the host date is 20240103.
    /// </remarks>
    [Fact]
    public void MissingEndIsTheHostLocalDate()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2024, 1, 2, 23, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone(
            "Test+9", TimeSpan.FromHours(9), "Test+9", "Test+9"));
        var request = new HistoricalDataRequest(["IBM US Equity"], ["PX_LAST"]);

        var sent = new HistoricalDataHandler(request, clock).CreateRequest(RefDataEvents.Service);

        Assert.Equal("20240102", Text(sent, "startDate"));
        Assert.Equal("20240103", Text(sent, "endDate"));
    }

    /// <summary>
    /// Each adjustment flag goes under its own element, and a flag set to false is sent
    /// as false.
    /// </summary>
    /// <remarks>
    /// Mutation: a false flag treated as unset, a flag sent under another element's
    /// name, or the calendar code left out.
    /// Oracle: the documented HistoricalDataRequest element names, with every flag
    /// that is not null sent. Each row sets one flag true and the
    /// other three false, so a swap of any two flags moves the true value.
    /// </remarks>
    [Theory]
    [InlineData("adjustmentNormal")]
    [InlineData("adjustmentAbnormal")]
    [InlineData("adjustmentSplit")]
    [InlineData("adjustmentFollowDPDF")]
    public void CreateRequestSendsEachAdjustmentFlagUnderItsName(string trueFlag)
    {
        var request = _request with
        {
            AdjustmentNormal = trueFlag == "adjustmentNormal",
            AdjustmentAbnormal = trueFlag == "adjustmentAbnormal",
            AdjustmentSplit = trueFlag == "adjustmentSplit",
            AdjustmentFollowDpdf = trueFlag == "adjustmentFollowDPDF",
            CalendarCodeOverride = "UK",
        };

        var sent = new HistoricalDataHandler(request).CreateRequest(RefDataEvents.Service);

        foreach (string name in (string[])[
            "adjustmentNormal", "adjustmentAbnormal", "adjustmentSplit", "adjustmentFollowDPDF",
        ])
        {
            Assert.Equal(name == trueFlag, Flag(sent, name));
        }

        Assert.Equal("UK", Text(sent, "calendarCodeOverride"));
    }

    /// <summary>An adjustment option left null is not sent.</summary>
    /// <remarks>
    /// Mutation: a null flag sent as false, or an empty calendar code sent.
    /// Oracle: the request element holds no adjustment flag when its option is null.
    /// </remarks>
    [Fact]
    public void CreateRequestOmitsNullAdjustmentOptions()
    {
        var sent = new HistoricalDataHandler(_request).CreateRequest(RefDataEvents.Service);

        foreach (string name in (string[])[
            "adjustmentNormal", "adjustmentAbnormal", "adjustmentSplit", "adjustmentFollowDPDF",
            "calendarCodeOverride",
        ])
        {
            Assert.False(sent.HasElement(Name.GetName(name)), name);
        }
    }

    /// <summary>A field error raises RequestFieldException when asked.</summary>
    /// <remarks>
    /// Mutation: the field errors of a historical securityData element dropped, so a
    /// bad field passes in silence, or the raise skipped.
    /// Oracle: one BAD_FLD entry for the field BAD_FIELD, the error the response XML
    /// holds.
    /// </remarks>
    [Fact]
    public void FieldErrorRaisesWhenRequested()
    {
        var handler = new HistoricalDataHandler(
            _request with { Fields = ["PX_LAST", "BAD_FIELD"], RaiseFieldError = true });
        handler.ProcessResponse(HistoricalResponse(Event.EventType.RESPONSE, FieldErrorXml));

        var error = Assert.Throws<RequestFieldException>(handler.ToResponse);
        Assert.Equal("BAD_FIELD", Assert.Single(error.Errors).Field);
    }

    /// <summary>A security error raises its own exception type.</summary>
    /// <remarks>
    /// Mutation: a bare Exception thrown, or the exception placed under
    /// SessionException, which a caller would read as a lost session.
    /// Oracle: RequestSecurityException, the class for one hand-built BAD_SEC record.
    /// </remarks>
    [Fact]
    public void SecurityErrorRaisesATypedException()
    {
        var handler = new HistoricalDataHandler(_request with { RaiseSecurityError = true });
        handler.ProcessResponse(HistoricalResponse(Event.EventType.RESPONSE, SecurityErrorXml));

        var error = Assert.Throws<RequestSecurityException>(handler.ToResponse);
        Assert.Equal(
            new SecurityError("BAD US Equity", "BAD_SEC", "Unknown", "INVALID_SECURITY"),
            Assert.Single(error.Errors));
        Assert.IsNotType<SessionException>(error, exactMatch: false);
    }

    /// <summary>A field error raises a RequestFieldException of its own.</summary>
    /// <remarks>
    /// Mutation: a bare Exception thrown, the two exception classes swapped, or the
    /// exception placed under SessionException.
    /// Oracle: RequestFieldException, the class for one hand-built BAD_FLD record.
    /// </remarks>
    [Fact]
    public void FieldErrorRaisesATypedException()
    {
        var handler = new HistoricalDataHandler(
            _request with { Fields = ["PX_LAST", "BAD_FIELD"], RaiseFieldError = true });
        handler.ProcessResponse(HistoricalResponse(Event.EventType.RESPONSE, FieldErrorXml));

        var error = Assert.Throws<RequestFieldException>(handler.ToResponse);
        Assert.Equal(
            new FieldError(
                "IBM US Equity", "BAD_FIELD", "BAD_FLD", "Field not valid", "INVALID_FIELD"),
            Assert.Single(error.Errors));
        Assert.IsNotType<SessionException>(error, exactMatch: false);
    }

    /// <summary>A security error is raised before a field error.</summary>
    /// <remarks>
    /// Mutation: the field check placed before the security check.
    /// Oracle: a security error takes precedence over a field error. Both kinds
    /// arrive and both flags are set.
    /// </remarks>
    [Fact]
    public void SecurityErrorIsRaisedBeforeFieldError()
    {
        var handler = new HistoricalDataHandler(
            _request with
            {
                Fields = ["PX_LAST", "BAD_FIELD"],
                RaiseSecurityError = true,
                RaiseFieldError = true,
            });
        handler.ProcessResponse(
            HistoricalResponse(Event.EventType.PARTIAL_RESPONSE, FieldErrorXml));
        handler.ProcessResponse(HistoricalResponse(Event.EventType.RESPONSE, SecurityErrorXml));

        Assert.Throws<RequestSecurityException>(handler.ToResponse);
    }

    /// <summary>A response with errors passes while the request raises none.</summary>
    /// <remarks>
    /// Mutation: the raise flags ignored, so every error throws.
    /// Oracle: the default flags are false, so the errors are recorded and none
    /// raises.
    /// </remarks>
    [Fact]
    public void ErrorsDoNotRaiseByDefault()
    {
        var handler = new HistoricalDataHandler(_request);
        handler.ProcessResponse(
            HistoricalResponse(Event.EventType.PARTIAL_RESPONSE, FieldErrorXml));
        handler.ProcessResponse(HistoricalResponse(Event.EventType.RESPONSE, SecurityErrorXml));

        var response = handler.ToResponse();

        Assert.Single(response.SecurityErrors);
        Assert.Single(response.FieldErrors);
    }

    private static bool Flag(Request request, string name) =>
        request.GetElement(Name.GetName(name)).GetValueAsBool();

    private static string Text(Request request, string name) =>
        request.GetElement(Name.GetName(name)).GetValueAsString();

    private static Event HistoricalResponse(Event.EventType eventType, string securityDataXml) =>
        RefDataEvents.Response(
            "HistoricalDataRequest", eventType, $"<Response>{securityDataXml}</Response>");
}
