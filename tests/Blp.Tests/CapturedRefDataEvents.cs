using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;

namespace Blp.Tests;

/// <summary>
/// Builds //blp/refdata response events on the shape of the real service, with no
/// terminal.
/// </summary>
/// <remarks>
/// The request types and enumerations are excerpts of the service definition the SDK
/// reads from a terminal. That definition holds no response types, so each response
/// type follows the message tree of a captured response, a choice as a terminal
/// sends it.
/// </remarks>
internal static class CapturedRefDataEvents
{
    private const string Schema = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ServiceDefinition name="blp.refdata" version="1.0.33.10">
          <service name="//blp/refdata" version="1.0.33.10">
            <operation name="BeqsRequest" serviceId="117">
              <request>BeqsRequest</request>
              <response>BeqsResponse</response>
            </operation>
            <operation name="ReferenceDataRequest" serviceId="84">
              <request>ReferenceDataRequest</request>
              <response>ReferenceDataResponse</response>
            </operation>
            <operation name="HistoricalDataRequest" serviceId="85">
              <request>HistoricalDataRequest</request>
              <response>HistoricalDataResponse</response>
            </operation>
            <operation name="IntradayBarRequest" serviceId="83">
              <request>IntradayBarRequest</request>
              <response>IntradayBarResponse</response>
            </operation>
            <operation name="IntradayTickRequest" serviceId="82">
              <request>IntradayTickRequest</request>
              <response>IntradayTickResponse</response>
            </operation>
          </service>
          <schema>
            <sequenceType name="FieldOverride">
              <element name="fieldId" type="String"/>
              <element name="value" type="String"/>
            </sequenceType>
            <sequenceType name="FieldOverrideRow">
              <element name="value" type="String" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="FieldOverrideTable">
              <element name="fieldId" type="String"/>
              <element name="row" type="FieldOverrideRow" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="ReferenceDataRequest">
              <element name="securities" type="String" maxOccurs="unbounded"/>
              <element name="fields" type="String" maxOccurs="unbounded"/>
              <element name="overrides" type="FieldOverride" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="useUTCTime" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="tableOverrides" type="FieldOverrideTable" minOccurs="0"
                maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="BeqsRequest">
              <element name="screenName" type="String" minOccurs="1" maxOccurs="1"/>
              <element name="screenType" type="ScreenValues" minOccurs="1" maxOccurs="1"/>
              <element name="Group" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="overrides" type="FieldOverride" minOccurs="0"
                maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="IntradayBarRequest">
              <element name="security" type="String"/>
              <element name="eventType" type="EventType"/>
              <element name="startDateTime" type="Datetime"/>
              <element name="endDateTime" type="Datetime"/>
              <element name="interval" type="Int32"/>
            </sequenceType>
            <sequenceType name="IntradayTickRequest">
              <element name="security" type="String"/>
              <element name="startDateTime" type="Datetime"/>
              <element name="endDateTime" type="Datetime"/>
              <element name="eventTypes" type="EventType" maxOccurs="unbounded"/>
              <element name="includeConditionCodes" type="Boolean" minOccurs="0"
                maxOccurs="1"/>
              <element name="includeBrokerCodes" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeRpsCodes" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeBicMicCodes" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeActionCodes" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeIndicatorCodes" type="Boolean" minOccurs="0"
                maxOccurs="1"/>
              <element name="includeTradeTime" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeEqRefPrice" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="includeTradeDate" type="Boolean" minOccurs="0" maxOccurs="1"/>
              <element name="filters" type="FDMFilterType" minOccurs="0"
                maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="FDMFilterType">
              <element name="filterName" type="String"/>
              <element name="fieldId" type="String"/>
              <element name="filterRule" type="String"/>
            </sequenceType>
            <sequenceType name="HistoricalDataRequest">
              <element name="securities" type="String" maxOccurs="unbounded"/>
              <element name="fields" type="String" maxOccurs="unbounded"/>
              <element name="startDate" type="String"/>
              <element name="endDate" type="String"/>
              <element name="periodicityAdjustment" type="PeriodicityAdjustmentType"
                minOccurs="0" maxOccurs="1"/>
              <element name="periodicitySelection" type="PeriodicitySelectionType"
                minOccurs="0" maxOccurs="1"/>
              <element name="currency" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="overrideOption" type="OverrideOption" minOccurs="0"
                maxOccurs="1"/>
              <element name="pricingOption" type="PricingOption" minOccurs="0" maxOccurs="1"/>
              <element name="nonTradingDayFillOption" type="NonTradingDayFillOption"
                minOccurs="0" maxOccurs="1"/>
              <element name="nonTradingDayFillMethod" type="NonTradingDayFillMethod"
                minOccurs="0" maxOccurs="1"/>
              <element name="maxDataPoints" type="Int32" minOccurs="0" maxOccurs="1"/>
              <element name="returnRelativeDate" type="Boolean"/>
              <element name="overrides" type="FieldOverride" maxOccurs="unbounded"/>
            </sequenceType>
            <enumerationType name="ScreenValues" type="String">
              <enumerator name="GLOBAL"><value><String>GLOBAL</String></value></enumerator>
              <enumerator name="PRIVATE"><value><String>PRIVATE</String></value></enumerator>
            </enumerationType>
            <enumerationType name="EventType" type="String">
              <enumerator name="TRADE"><value><String>TRADE</String></value></enumerator>
              <enumerator name="BID"><value><String>BID</String></value></enumerator>
              <enumerator name="ASK"><value><String>ASK</String></value></enumerator>
            </enumerationType>
            <enumerationType name="PeriodicityAdjustmentType" type="String">
              <enumerator name="CALENDAR"><value><String>CALENDAR</String></value></enumerator>
            </enumerationType>
            <enumerationType name="PeriodicitySelectionType" type="String">
              <enumerator name="DAILY"><value><String>DAILY</String></value></enumerator>
              <enumerator name="WEEKLY"><value><String>WEEKLY</String></value></enumerator>
              <enumerator name="QUARTERLY"><value><String>QUARTERLY</String></value></enumerator>
            </enumerationType>
            <enumerationType name="OverrideOption" type="String">
              <enumerator name="OVERRIDE_OPTION_GPA">
                <value><String>OVERRIDE_OPTION_GPA</String></value>
              </enumerator>
            </enumerationType>
            <enumerationType name="PricingOption" type="String">
              <enumerator name="PRICING_OPTION_PRICE">
                <value><String>PRICING_OPTION_PRICE</String></value>
              </enumerator>
            </enumerationType>
            <enumerationType name="NonTradingDayFillOption" type="String">
              <enumerator name="ALL_CALENDAR_DAYS">
                <value><String>ALL_CALENDAR_DAYS</String></value>
              </enumerator>
            </enumerationType>
            <enumerationType name="NonTradingDayFillMethod" type="String">
              <enumerator name="NIL_VALUE"><value><String>NIL_VALUE</String></value></enumerator>
            </enumerationType>
            <sequenceType name="ErrorInfo">
              <element name="source" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="code" type="Int32" minOccurs="0" maxOccurs="1"/>
              <element name="category" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="message" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="subcategory" type="String" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="FieldException">
              <element name="fieldId" type="String"/>
              <element name="errorInfo" type="ErrorInfo"/>
            </sequenceType>
            <choiceType name="BeqsResponse">
              <element name="responseError" type="ErrorInfo"/>
            </choiceType>
            <choiceType name="ReferenceDataResponse">
              <element name="responseError" type="ErrorInfo"/>
              <element name="securityData" type="ReferenceSecurityData" maxOccurs="unbounded"/>
            </choiceType>
            <sequenceType name="ReferenceSecurityData">
              <element name="security" type="String"/>
              <element name="eidData" type="Int32" minOccurs="0" maxOccurs="unbounded"/>
              <element name="fieldExceptions" type="FieldException" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="fieldData" type="ReferenceFieldData"/>
              <element name="securityError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
              <element name="sequenceNumber" type="Int32"/>
            </sequenceType>
            <sequenceType name="ReferenceFieldData">
              <element name="PX_LAST" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="VOLUME" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="NAME" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="CRNCY" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="EQY_SH_OUT" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="CUR_MKT_CAP" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_UPDATE_DT" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="TIME" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_UPDATE" type="Datetime" minOccurs="0" maxOccurs="1"/>
              <element name="DVD_EX_DT" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_TRADE_TIME" type="Time" minOccurs="0" maxOccurs="1"/>
              <element name="BEST_EPS" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="DVD_HIST_ALL" type="DividendRow" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="INDX_MEMBERS" type="IndexMemberRow" minOccurs="0"
                maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="DividendRow">
              <element name="Declared Date" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="Ex-Date" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="Record Date" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="Payable Date" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="Dividend Amount" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="Dividend Frequency" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="Dividend Type" type="String" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="IndexMemberRow">
              <element name="Member Ticker and Exchange Code" type="String" minOccurs="0"
                maxOccurs="1"/>
            </sequenceType>
            <choiceType name="HistoricalDataResponse">
              <element name="responseError" type="ErrorInfo"/>
              <element name="securityData" type="HistoricalSecurityData"/>
            </choiceType>
            <sequenceType name="HistoricalSecurityData">
              <element name="security" type="String"/>
              <element name="eidData" type="Int32" minOccurs="0" maxOccurs="unbounded"/>
              <element name="fieldExceptions" type="FieldException" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="fieldData" type="HistoricalFieldData" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="sequenceNumber" type="Int32"/>
              <element name="securityError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="HistoricalFieldData">
              <element name="date" type="Date"/>
              <element name="PX_LAST" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="VOLUME" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="BEST_EPS" type="Float64" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <choiceType name="IntradayTickResponse">
              <element name="responseError" type="ErrorInfo"/>
              <element name="tickData" type="IntradayTickData"/>
            </choiceType>
            <sequenceType name="IntradayTickData">
              <element name="eidData" type="Int32" minOccurs="0" maxOccurs="unbounded"/>
              <element name="tickData" type="IntradayTick" minOccurs="0"
                maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="IntradayTick">
              <element name="time" type="Datetime"/>
              <element name="type" type="EventType"/>
              <element name="value" type="Float64"/>
              <element name="size" type="Int32"/>
              <element name="conditionCodes" type="String" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <choiceType name="IntradayBarResponse">
              <element name="responseError" type="ErrorInfo"/>
              <element name="barData" type="IntradayBarData"/>
            </choiceType>
            <sequenceType name="IntradayBarData">
              <element name="eidData" type="Int32" minOccurs="0" maxOccurs="unbounded"/>
              <element name="barTickData" type="IntradayBar" minOccurs="0"
                maxOccurs="unbounded"/>
              <element name="delayedSecurity" type="Boolean"/>
            </sequenceType>
            <sequenceType name="IntradayBar">
              <element name="time" type="Datetime"/>
              <element name="open" type="Float64"/>
              <element name="high" type="Float64"/>
              <element name="low" type="Float64"/>
              <element name="close" type="Float64"/>
              <element name="volume" type="Int64"/>
              <element name="numEvents" type="Int32"/>
              <element name="value" type="Float64"/>
            </sequenceType>
          </schema>
        </ServiceDefinition>
        """;

    /// <summary>The deserialized //blp/refdata service.</summary>
    public static Service Service { get; } = RefDataEvents.DeserializeService(Schema);

    /// <summary>An event holding one response message of an operation.</summary>
    /// <param name="operation">An operation name, such as ReferenceDataRequest.</param>
    /// <param name="eventType">PARTIAL_RESPONSE or RESPONSE.</param>
    /// <param name="responseXml">
    /// The message, its root named for the response type.
    /// </param>
    /// <returns>The event.</returns>
    public static Event Response(string operation, Event.EventType eventType, string responseXml)
    {
        var responseEvent = TestUtil.CreateEvent(eventType);
        ResponseFormatter(responseEvent, operation).FormatMessageXml(responseXml);
        return responseEvent;
    }

    /// <summary>
    /// An event holding one response message of an operation, set through the typed
    /// formatter, for element names XML cannot carry.
    /// </summary>
    /// <param name="operation">An operation name, such as ReferenceDataRequest.</param>
    /// <param name="eventType">PARTIAL_RESPONSE or RESPONSE.</param>
    /// <param name="setElements">Sets the message's elements, at its root.</param>
    /// <returns>The event.</returns>
    public static Event Response(
        string operation, Event.EventType eventType, Action<IMessageFormatter> setElements)
    {
        var responseEvent = TestUtil.CreateEvent(eventType);
        setElements(ResponseFormatter(responseEvent, operation));
        return responseEvent;
    }

    /// <summary>
    /// A request as the SDK prints it, with "\n" line ends and no final one.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The printed text.</returns>
    public static string PrintedText(Request request) =>
        $"{request}".ReplaceLineEndings("\n").TrimEnd();

    private static IMessageFormatter ResponseFormatter(Event responseEvent, string operation) =>
        TestUtil.AppendMessage(
            responseEvent, Service.GetOperation(Name.GetName(operation)).GetResponseDefinition(0));
}
