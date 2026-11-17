using Bloomberglp.Blpapi;
using Blp.ReferenceData;

namespace Blp.Tests.ReferenceData;

public class ReferenceDataHandlerTests
{
    private static readonly ReferenceDataRequest _request = new(
        ["IBM US Equity", "BAD US Equity"], ["PX_LAST", "NAME"])
    {
        TimeZone = TimeZoneInfo.Utc,
    };

    /// <summary>
    /// CreateRequest carries the securities, fields, overrides and UTC times.
    /// </summary>
    /// <remarks>
    /// Mutation: overrides dropped, a field id and value swapped, or useUTCTime unset.
    /// Oracle: the request's own inputs.
    /// </remarks>
    [Fact]
    public void CreateRequestCarriesEveryInput()
    {
        var request = _request with
        {
            Overrides = new Dictionary<string, string> { ["EQY_FUND_CRNCY"] = "EUR" },
        };

        var bloombergRequest = new ReferenceDataHandler(request)
            .CreateRequest(RefDataEvents.Service);

        var securities = bloombergRequest.GetElement(Name.GetName("securities"));
        Assert.Equal(
            ["IBM US Equity", "BAD US Equity"],
            Enumerable.Range(0, securities.NumValues).Select(securities.GetValueAsString));
        Assert.Equal(2, bloombergRequest.GetElement(Name.GetName("fields")).NumValues);
        Assert.True(bloombergRequest.GetElement(Name.GetName("useUTCTime")).GetValueAsBool());
        var entry = bloombergRequest.GetElement(Name.GetName("overrides")).GetValueAsElement(0);
        Assert.Equal("EQY_FUND_CRNCY", entry.GetElementAsString(Name.GetName("fieldId")));
        Assert.Equal("EUR", entry.GetElementAsString(Name.GetName("value")));
    }

    /// <summary>
    /// A response yields values, security errors and field errors, each where it
    /// belongs.
    /// </summary>
    /// <remarks>
    /// Mutation: a rejected security kept in Values, a requested field left out where
    /// Bloomberg sent none, or fieldExceptions ignored.
    /// Oracle: the hand-written response.
    /// </remarks>
    [Fact]
    public void ResponseSplitsValuesAndErrors()
    {
        var handler = new ReferenceDataHandler(_request);

        handler.ProcessResponse(RefDataEvents.Response(
            "ReferenceDataRequest",
            Event.EventType.RESPONSE,
            """
            <Response>
              <securityData>
                <security>IBM US Equity</security>
                <fieldExceptions>
                  <fieldId>NAME</fieldId>
                  <errorInfo><category>BAD_FLD</category><message>Unknown</message></errorInfo>
                </fieldExceptions>
                <fieldData><PX_LAST>7283.5</PX_LAST></fieldData>
              </securityData>
              <securityData>
                <security>BAD US Equity</security>
                <securityError>
                  <category>BAD_SEC</category>
                  <message>Unknown security</message>
                  <subcategory>INVALID_SECURITY</subcategory>
                </securityError>
              </securityData>
            </Response>
            """));
        var response = handler.ToResponse();

        var values = Assert.Single(response.Values);
        Assert.Equal("IBM US Equity", values.Key);
        Assert.Equal(7283.5, values.Value["PX_LAST"]);
        Assert.True(values.Value.ContainsKey("NAME"));
        Assert.Null(values.Value["NAME"]);
        Assert.Equal(
            new SecurityError("BAD US Equity", "BAD_SEC", "Unknown security", "INVALID_SECURITY"),
            Assert.Single(response.SecurityErrors));
        Assert.Equal(
            new FieldError("IBM US Equity", "NAME", "BAD_FLD", "Unknown", null),
            Assert.Single(response.FieldErrors));
    }

    /// <summary>
    /// A security sent again in a later message takes the later values, as the Python
    /// blp's dict update does.
    /// </summary>
    /// <remarks>
    /// Mutation: a security already seen skipped, so the first message's value stays.
    /// Oracle: the Python blp's on_security_data, which updates the ticker's dict.
    /// </remarks>
    [Fact]
    public void LaterMessageUpdatesTheSecurity()
    {
        var handler = new ReferenceDataHandler(_request);

        handler.ProcessResponse(OneSecurity(Event.EventType.PARTIAL_RESPONSE, "1.5"));
        handler.ProcessResponse(OneSecurity(Event.EventType.RESPONSE, "2.5"));

        Assert.Equal(2.5, Assert.Single(handler.ToResponse().Values).Value["PX_LAST"]);
    }

    /// <summary>A responseError raises ResponseException with its category.</summary>
    /// <remarks>
    /// Mutation: the responseError ignored, or category and subcategory swapped.
    /// Oracle: the hand-written responseError.
    /// </remarks>
    [Fact]
    public void ResponseErrorRaises()
    {
        var handler = new ReferenceDataHandler(_request);
        var responseEvent = RefDataEvents.Response(
            "ReferenceDataRequest",
            Event.EventType.RESPONSE,
            """
            <Response>
              <responseError>
                <category>LIMIT</category>
                <message>Daily limit reached</message>
                <subcategory>DAILY_LIMIT</subcategory>
              </responseError>
            </Response>
            """);

        var error = Assert.Throws<ResponseException>(() => handler.ProcessResponse(responseEvent));
        Assert.Equal("LIMIT", error.Category);
        Assert.Equal("DAILY_LIMIT", error.Subcategory);
    }

    private static Event OneSecurity(Event.EventType eventType, string price) =>
        RefDataEvents.Response(
            "ReferenceDataRequest",
            eventType,
            $"""
            <Response>
              <securityData>
                <security>IBM US Equity</security>
                <fieldData><PX_LAST>{price}</PX_LAST></fieldData>
              </securityData>
            </Response>
            """);
}
