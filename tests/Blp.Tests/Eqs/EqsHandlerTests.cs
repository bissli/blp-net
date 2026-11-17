using Bloomberglp.Blpapi;
using Blp.Eqs;

namespace Blp.Tests.Eqs;

public class EqsHandlerTests
{
    private static readonly EqsRequest _request = new("Screen")
    {
        TimeZone = TimeZoneInfo.Utc,
    };

    /// <summary>
    /// CreateRequest carries the screen name, type, group and as-of date.
    /// </summary>
    /// <remarks>
    /// Mutation: the screen type sent as its C# name, the group dropped, or the as-of
    /// date in another format or element.
    /// Oracle: Bloomberg's GLOBAL and PRIVATE values and the PiTDate override's
    /// yyyyMMdd form.
    /// </remarks>
    [Fact]
    public void CreateRequestCarriesEveryInput()
    {
        var request = _request with
        {
            ScreenType = EqsScreenType.Private,
            Group = "Banks",
            AsOf = new DateOnly(2024, 3, 1),
        };

        var element = new EqsHandler(request).CreateRequest(RefDataEvents.Service).AsElement;

        Assert.Equal("Screen", element.GetElementAsString(Name.GetName("screenName")));
        Assert.Equal("PRIVATE", element.GetElementAsString(Name.GetName("screenType")));
        Assert.Equal("Banks", element.GetElementAsString(Name.GetName("Group")));
        var entry = element.GetElement(Name.GetName("overrides")).GetValueAsElement(0);
        Assert.Equal("PiTDate", entry.GetElementAsString(Name.GetName("fieldId")));
        Assert.Equal("20240301", entry.GetElementAsString(Name.GetName("value")));
    }

    /// <summary>
    /// The defaults send a GLOBAL screen in group General with no override, and an
    /// empty group sends none.
    /// </summary>
    /// <remarks>
    /// Mutation: another default type or group, an override sent with no as-of date,
    /// or an empty group sent.
    /// Oracle: the defaults EqsRequest declares, Global and "General".
    /// </remarks>
    [Fact]
    public void DefaultsSendGlobalGeneralAndEmptyGroupSendsNone()
    {
        var defaults = new EqsHandler(_request).CreateRequest(RefDataEvents.Service).AsElement;
        var noGroup = new EqsHandler(_request with { Group = "" })
            .CreateRequest(RefDataEvents.Service).AsElement;

        Assert.Equal("GLOBAL", defaults.GetElementAsString(Name.GetName("screenType")));
        Assert.Equal("General", defaults.GetElementAsString(Name.GetName("Group")));
        Assert.Equal(0, defaults.GetElement(Name.GetName("overrides")).NumValues);
        Assert.False(noGroup.HasElement(Name.GetName("Group"), true));
    }

    /// <summary>A response stores each security's columns under its ticker.</summary>
    /// <remarks>
    /// Mutation: only the first security kept, or the columns read from a fixed list
    /// in place of the ones the screen sends.
    /// Oracle: a hand-built securityData array of two securities.
    /// </remarks>
    [Fact]
    public void ScreenerResponseFillsRows()
    {
        var handler = new EqsHandler(_request);

        handler.ProcessResponse(RefDataEvents.Response(
            "BeqsRequest",
            Event.EventType.RESPONSE,
            """
            <Response>
              <data>
                <securityData>
                  <security>IBM US Equity</security>
                  <fieldData><Ticker>IBM</Ticker></fieldData>
                </securityData>
                <securityData>
                  <security>AAPL US Equity</security>
                  <fieldData><Ticker>AAPL</Ticker></fieldData>
                </securityData>
              </data>
            </Response>
            """));

        var values = handler.ToResponse().Values;
        Assert.Equal(["IBM US Equity", "AAPL US Equity"], values.Keys);
        Assert.Equal("IBM", Assert.Single(values["IBM US Equity"]).Value);
        Assert.Equal("AAPL", Assert.Single(values["AAPL US Equity"]).Value);
    }

    /// <summary>
    /// A response yields values, security errors and field errors, each where it
    /// belongs.
    /// </summary>
    /// <remarks>
    /// Mutation: a rejected security kept in Values, or fieldExceptions ignored.
    /// Oracle: the hand-written response.
    /// </remarks>
    [Fact]
    public void ResponseSplitsValuesAndErrors()
    {
        var handler = new EqsHandler(_request);

        handler.ProcessResponse(RefDataEvents.Response(
            "BeqsRequest",
            Event.EventType.RESPONSE,
            """
            <Response>
              <data>
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
                    <category>BAD_SEC</category><message>Unknown</message>
                  </securityError>
                </securityData>
              </data>
            </Response>
            """));
        var response = handler.ToResponse();

        var values = Assert.Single(response.Values);
        Assert.Equal("IBM US Equity", values.Key);
        Assert.Equal(7283.5, Assert.Single(values.Value).Value);
        Assert.Equal("BAD US Equity", Assert.Single(response.SecurityErrors).Security);
        Assert.Equal(
            new FieldError("IBM US Equity", "NAME", "BAD_FLD", "Unknown", null),
            Assert.Single(response.FieldErrors));
    }

    /// <summary>A screen Bloomberg cannot run raises ResponseException.</summary>
    /// <remarks>
    /// Mutation: the response read without its responseError check, which throws on the
    /// missing data element instead.
    /// Oracle: the responseError a terminal returned for a screen with no data.
    /// </remarks>
    [Fact]
    public void ResponseErrorRaises()
    {
        var handler = new EqsHandler(_request);
        var responseEvent = RefDataEvents.Response(
            "BeqsRequest",
            Event.EventType.RESPONSE,
            """
            <Response>
              <responseError>
                <source>sendRequest</source>
                <code>4080</code>
                <category>apieqs</category>
                <message>madiResponse does not contain data</message>
              </responseError>
            </Response>
            """);

        var error = Assert.Throws<ResponseException>(() => handler.ProcessResponse(responseEvent));
        Assert.Equal("apieqs", error.Category);
    }
}
