using Bloomberglp.Blpapi;
using Blp.HistoricalData;

namespace Blp.Tests.HistoricalData;

/// <summary>
/// <see cref="HistoricalDataHandler"/> over excerpts of responses a terminal sent, on
/// the real service shape.
/// </summary>
public class HistoricalDataCaptureTests
{
    /// <summary>
    /// CreateRequest sends the currency, fill options and point cap a terminal was
    /// sent, with the daily period and the relative-date flag it defaults.
    /// </summary>
    /// <remarks>
    /// Mutation: the fill option and method swapped, or the periodicity left unset.
    /// Oracle: the printed request of the hist_options capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedOptionsRequest()
    {
        var handler = new HistoricalDataHandler(
            new HistoricalDataRequest(
                ["IBM US Equity"], ["PX_LAST"], new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 31))
            {
                Currency = "EUR",
                NonTradingDayFillOption = NonTradingDayFillOption.AllCalendarDays,
                NonTradingDayFillMethod = NonTradingDayFillMethod.NilValue,
                MaxDataPoints = 5,
            });

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            HistoricalDataRequest = {
                securities[] = {
                    IBM US Equity
                }
                fields[] = {
                    PX_LAST
                }
                startDate = "20240301"
                endDate = "20240331"
                periodicitySelection = DAILY
                currency = "EUR"
                nonTradingDayFillOption = ALL_CALENDAR_DAYS
                nonTradingDayFillMethod = NIL_VALUE
                maxDataPoints = 5
                returnRelativeDate = false
                overrides[] = {
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// CreateRequest sends the period, its adjustment, the override and pricing options
    /// and a field override under their Bloomberg names.
    /// </summary>
    /// <remarks>
    /// Mutation: the OVERRIDE_OPTION_ or PRICING_OPTION_ prefix dropped, QUARTERLY sent
    /// as its C# name, or the field override left out.
    /// Oracle: the printed request of the hist_pricing_options capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedPricingRequest()
    {
        var handler = new HistoricalDataHandler(
            new HistoricalDataRequest(
                ["IBM US Equity"],
                ["PX_LAST", "BEST_EPS"],
                new DateOnly(2023, 1, 1),
                new DateOnly(2024, 12, 31))
            {
                Period = Periodicity.Quarterly,
                PeriodAdjustment = PeriodicityAdjustment.Calendar,
                OverrideOption = OverrideOption.Gpa,
                PricingOption = PricingOption.Price,
                Overrides = new Dictionary<string, string> { ["BEST_FPERIOD_OVERRIDE"] = "1BF" },
            });

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            HistoricalDataRequest = {
                securities[] = {
                    IBM US Equity
                }
                fields[] = {
                    PX_LAST, BEST_EPS
                }
                startDate = "20230101"
                endDate = "20241231"
                periodicityAdjustment = CALENDAR
                periodicitySelection = QUARTERLY
                overrideOption = OVERRIDE_OPTION_GPA
                pricingOption = PRICING_OPTION_PRICE
                returnRelativeDate = false
                overrides[] = {
                    overrides = {
                        fieldId = "BEST_FPERIOD_OVERRIDE"
                        value = "1BF"
                    }
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// A partial response and the final one each add their security, with a date and
    /// float values per point.
    /// </summary>
    /// <remarks>
    /// Mutation: only the final message kept, VOLUME read as an integer, or a point's
    /// date taken from the request's start.
    /// Oracle: the first two points of each security in the hist_daily capture, which
    /// sent IBM in a PARTIAL_RESPONSE and SPX in the RESPONSE.
    /// </remarks>
    [Fact]
    public void PartialAndFinalResponsesGiveTypedPointsPerSecurity()
    {
        var handler = new HistoricalDataHandler(
            new HistoricalDataRequest(
                ["IBM US Equity", "SPX Index"],
                ["PX_LAST", "VOLUME"],
                new DateOnly(2024, 1, 2),
                new DateOnly(2024, 3, 29)));

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "HistoricalDataRequest",
            Event.EventType.PARTIAL_RESPONSE,
            """
            <HistoricalDataResponse>
              <securityData>
                <security>IBM US Equity</security>
                <fieldData><date>2024-01-02</date><PX_LAST>161.5</PX_LAST>
                  <VOLUME>3825044</VOLUME></fieldData>
                <fieldData><date>2024-01-03</date><PX_LAST>160.1</PX_LAST>
                  <VOLUME>4086065</VOLUME></fieldData>
                <sequenceNumber>0</sequenceNumber>
              </securityData>
            </HistoricalDataResponse>
            """));
        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "HistoricalDataRequest",
            Event.EventType.RESPONSE,
            """
            <HistoricalDataResponse>
              <securityData>
                <security>SPX Index</security>
                <fieldData><date>2024-01-02</date><PX_LAST>4742.83</PX_LAST>
                  <VOLUME>707023582</VOLUME></fieldData>
                <fieldData><date>2024-01-03</date><PX_LAST>4704.81</PX_LAST>
                  <VOLUME>696024599</VOLUME></fieldData>
                <sequenceNumber>1</sequenceNumber>
              </securityData>
            </HistoricalDataResponse>
            """));
        var rows = handler.ToResponse().Rows;

        Assert.Equal(["IBM US Equity", "SPX Index"], rows.Keys);
        Assert.Equal(
            [
                (new DateOnly(2024, 1, 2), (object?)161.5, 3825044.0),
                (new DateOnly(2024, 1, 3), 160.1, 4086065.0),
            ],
            rows["IBM US Equity"].Select(
                row => (row.Date, row.Values["PX_LAST"], row.Values["VOLUME"])));
        Assert.Equal(
            [
                (new DateOnly(2024, 1, 2), (object?)4742.83, 707023582.0),
                (new DateOnly(2024, 1, 3), 4704.81, 696024599.0),
            ],
            rows["SPX Index"].Select(
                row => (row.Date, row.Values["PX_LAST"], row.Values["VOLUME"])));
    }

    /// <summary>
    /// A day Bloomberg fills with nothing is a point with a null value.
    /// </summary>
    /// <remarks>
    /// Mutation: a point with no value dropped, or its value given as 0 or NaN.
    /// Oracle: points 2 to 4 of the hist_options capture, where 2024-03-29 and
    /// 2024-03-30 carry a date only.
    /// </remarks>
    [Fact]
    public void NilFilledDaysAreNullPoints()
    {
        var handler = new HistoricalDataHandler(
            new HistoricalDataRequest(
                ["IBM US Equity"],
                ["PX_LAST"],
                new DateOnly(2024, 3, 1),
                new DateOnly(2024, 3, 31)));

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "HistoricalDataRequest",
            Event.EventType.RESPONSE,
            """
            <HistoricalDataResponse>
              <securityData>
                <security>IBM US Equity</security>
                <fieldData><date>2024-03-28</date><PX_LAST>176.88033</PX_LAST></fieldData>
                <fieldData><date>2024-03-29</date></fieldData>
                <fieldData><date>2024-03-30</date></fieldData>
                <sequenceNumber>0</sequenceNumber>
              </securityData>
            </HistoricalDataResponse>
            """));

        var rows = Assert.Single(handler.ToResponse().Rows).Value;
        Assert.Equal(
            [
                (new DateOnly(2024, 3, 28), (object?)176.88033),
                (new DateOnly(2024, 3, 29), null),
                (new DateOnly(2024, 3, 30), null),
            ],
            rows.Select(row => (row.Date, row.Values["PX_LAST"])));
    }

    /// <summary>
    /// A rejected field is null in every point with its field error, and a rejected
    /// security has no points and a security error.
    /// </summary>
    /// <remarks>
    /// Mutation: the rejected security kept with no points, the rejected field missing
    /// from a point, or the field error read from the security that follows.
    /// Oracle: the hist_weekly_errors capture, which sent IBM in a PARTIAL_RESPONSE
    /// with a field exception and NOTATICKER in the RESPONSE. Its source elements and
    /// the [nid:] suffix of the security error's message are left out of the excerpt.
    /// </remarks>
    [Fact]
    public void RejectedFieldAndSecurityGiveErrors()
    {
        var handler = new HistoricalDataHandler(
            new HistoricalDataRequest(
                ["IBM US Equity", "NOTATICKER US Equity"],
                ["PX_LAST", "NOT_A_FIELD"],
                new DateOnly(2024, 1, 2),
                new DateOnly(2024, 3, 29))
            {
                Period = Periodicity.Weekly,
            });

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "HistoricalDataRequest",
            Event.EventType.PARTIAL_RESPONSE,
            """
            <HistoricalDataResponse>
              <securityData>
                <security>IBM US Equity</security>
                <fieldExceptions>
                  <fieldId>NOT_A_FIELD</fieldId>
                  <errorInfo>
                    <code>1</code>
                    <category>BAD_FLD</category>
                    <message>Invalid field</message>
                    <subcategory>NOT_APPLICABLE_TO_HIST_DATA</subcategory>
                  </errorInfo>
                </fieldExceptions>
                <fieldData><date>2024-01-05</date><PX_LAST>159.16</PX_LAST></fieldData>
                <fieldData><date>2024-01-12</date><PX_LAST>165.8</PX_LAST></fieldData>
                <sequenceNumber>0</sequenceNumber>
              </securityData>
            </HistoricalDataResponse>
            """));
        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "HistoricalDataRequest",
            Event.EventType.RESPONSE,
            """
            <HistoricalDataResponse>
              <securityData>
                <security>NOTATICKER US Equity</security>
                <sequenceNumber>1</sequenceNumber>
                <securityError>
                  <code>15</code>
                  <category>BAD_SEC</category>
                  <message>Unknown/Invalid securityInvalid Security</message>
                  <subcategory>INVALID_SECURITY</subcategory>
                </securityError>
              </securityData>
            </HistoricalDataResponse>
            """));
        var response = handler.ToResponse();

        var rows = Assert.Single(response.Rows);
        Assert.Equal("IBM US Equity", rows.Key);
        Assert.Equal(
            [
                (new DateOnly(2024, 1, 5), (object?)159.16, null),
                (new DateOnly(2024, 1, 12), 165.8, null),
            ],
            rows.Value.Select(row => (row.Date, row.Values["PX_LAST"], row.Values["NOT_A_FIELD"])));
        Assert.Equal(
            new FieldError(
                "IBM US Equity",
                "NOT_A_FIELD",
                "BAD_FLD",
                "Invalid field",
                "NOT_APPLICABLE_TO_HIST_DATA"),
            Assert.Single(response.FieldErrors));
        Assert.Equal(
            new SecurityError(
                "NOTATICKER US Equity",
                "BAD_SEC",
                "Unknown/Invalid securityInvalid Security",
                "INVALID_SECURITY"),
            Assert.Single(response.SecurityErrors));
    }
}
