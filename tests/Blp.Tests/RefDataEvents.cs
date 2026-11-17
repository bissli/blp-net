using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;

namespace Blp.Tests;

/// <summary>Builds //blp/refdata response events from XML, with no terminal.</summary>
internal static class RefDataEvents
{
    private const string Schema = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ServiceDefinition name="blp.refdata" version="1.0.1.0">
          <service name="//blp/refdata" version="1.0.0.0">
            <operation name="ReferenceDataRequest" serviceId="84">
              <request>ReferenceDataRequest</request>
              <response>Response</response>
              <responseSelection>ReferenceDataResponse</responseSelection>
            </operation>
            <operation name="HistoricalDataRequest" serviceId="84">
              <request>HistoricalDataRequest</request>
              <response>Response</response>
              <responseSelection>HistoricalDataResponse</responseSelection>
            </operation>
          </service>
          <schema>
            <sequenceType name="ReferenceDataRequest">
              <element name="securities" type="String" maxOccurs="unbounded"/>
              <element name="fields" type="String" maxOccurs="unbounded"/>
              <element name="overrides" type="FieldOverride" minOccurs="0" maxOccurs="unbounded"/>
              <element name="useUTCTime" type="Boolean" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="HistoricalDataRequest">
              <element name="securities" type="String" maxOccurs="unbounded"/>
              <element name="fields" type="String" maxOccurs="unbounded"/>
              <element name="startDate" type="String"/>
              <element name="endDate" type="String"/>
              <element name="periodicitySelection" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="periodicityAdjustment" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="currency" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="overrideOption" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="pricingOption" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="nonTradingDayFillOption" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="nonTradingDayFillMethod" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="maxDataPoints" type="Int32" minOccurs="0" maxOccurs="1"/>
              <element name="overrides" type="FieldOverride" minOccurs="0" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="FieldOverride">
              <element name="fieldId" type="String"/>
              <element name="value" type="String"/>
            </sequenceType>
            <choiceType name="Response">
              <element name="ReferenceDataResponse" type="ReferenceDataResponseType"/>
              <element name="HistoricalDataResponse" type="HistoricalDataResponseType"/>
            </choiceType>
            <sequenceType name="HistoricalDataResponseType">
              <element name="responseError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
              <element name="securityData" type="HistoricalSecurityData"/>
            </sequenceType>
            <sequenceType name="HistoricalSecurityData">
              <element name="security" type="String"/>
              <element name="securityError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
              <element name="fieldExceptions" type="FieldException"
                minOccurs="0" maxOccurs="unbounded"/>
              <element name="fieldData" type="HistoricalPoint"
                minOccurs="0" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="HistoricalPoint">
              <element name="date" type="Date"/>
              <element name="PX_LAST" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="VOLUME" type="Int64" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="ReferenceDataResponseType">
              <element name="responseError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
              <element name="securityData" type="ReferenceSecurityData"
                minOccurs="0" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="ReferenceSecurityData">
              <element name="security" type="String"/>
              <element name="securityError" type="ErrorInfo" minOccurs="0" maxOccurs="1"/>
              <element name="fieldExceptions" type="FieldException"
                minOccurs="0" maxOccurs="unbounded"/>
              <element name="fieldData" type="FieldData" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="FieldData">
              <element name="PX_LAST" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="VOLUME" type="Int64" minOccurs="0" maxOccurs="1"/>
              <element name="NAME" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_DATE" type="Date" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_STAMP" type="Datetime" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_TIME" type="Time" minOccurs="0" maxOccurs="1"/>
              <element name="HOLDERS" type="Holder" minOccurs="0" maxOccurs="unbounded"/>
              <element name="TOP_HOLDER" type="Holder" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="Holder">
              <element name="name" type="String"/>
              <element name="weight" type="Float64" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="FieldException">
              <element name="fieldId" type="String"/>
              <element name="errorInfo" type="ErrorInfo"/>
            </sequenceType>
            <sequenceType name="ErrorInfo">
              <element name="source" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="code" type="Int64" minOccurs="0" maxOccurs="1"/>
              <element name="category" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="message" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="subcategory" type="String" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
          </schema>
        </ServiceDefinition>
        """;

    private static readonly Service _service = TestUtil.DeserializeService(Schema);

    /// <summary>The deserialized //blp/refdata service.</summary>
    public static Service Service => _service;

    /// <summary>An event holding one response message of an operation.</summary>
    /// <param name="operation">ReferenceDataRequest or HistoricalDataRequest.</param>
    /// <param name="eventType">PARTIAL_RESPONSE or RESPONSE.</param>
    /// <param name="responseXml">The message as a &lt;Response&gt; element.</param>
    /// <param name="correlationId">The id the message answers. Null sets none.</param>
    /// <returns>The event.</returns>
    public static Event Response(
        string operation,
        Event.EventType eventType,
        string responseXml,
        CorrelationID? correlationId = null)
    {
        var definition = _service.GetOperation(Name.GetName(operation)).GetResponseDefinition(0);
        return OneMessage(eventType, definition, responseXml, correlationId);
    }

    /// <summary>An event holding one admin message, such as RequestFailure.</summary>
    /// <param name="eventType">REQUEST_STATUS or SESSION_STATUS.</param>
    /// <param name="messageXml">The message, its root named for its type.</param>
    /// <param name="correlationId">The id the message answers. Null sets none.</param>
    /// <returns>The event.</returns>
    public static Event Admin(
        Event.EventType eventType, string messageXml, CorrelationID? correlationId = null)
    {
        string messageType = System.Xml.Linq.XElement.Parse(messageXml).Name.LocalName;
        var definition = TestUtil.GetAdminMessageDefinition(Name.GetName(messageType));
        return OneMessage(eventType, definition, messageXml, correlationId);
    }

    /// <summary>The fieldData element of a one-security response.</summary>
    /// <param name="fieldDataXml">The children of fieldData, as XML.</param>
    /// <returns>The fieldData element.</returns>
    public static Element FieldData(string fieldDataXml)
    {
        var responseEvent = Response(
            "ReferenceDataRequest",
            Event.EventType.RESPONSE,
            $"""
            <Response>
              <securityData>
                <security>IBM US Equity</security>
                <fieldData>{fieldDataXml}</fieldData>
              </securityData>
            </Response>
            """);
        return responseEvent.First()
            .GetElement(Name.GetName("securityData"))
            .GetValueAsElement(0)
            .GetElement(Name.GetName("fieldData"));
    }

    /// <summary>
    /// The fieldData element of a one-security response, set through the typed
    /// formatter, for values XML cannot express.
    /// </summary>
    /// <param name="setFields">Sets fields through the formatter, at fieldData.</param>
    /// <returns>The fieldData element.</returns>
    public static Element FieldData(Action<IMessageFormatter> setFields)
    {
        var responseEvent = TestUtil.CreateEvent(Event.EventType.RESPONSE);
        var definition = _service
            .GetOperation(Name.GetName("ReferenceDataRequest"))
            .GetResponseDefinition(0);
        var formatter = TestUtil.AppendMessage(responseEvent, definition);
        formatter.PushElement(Name.GetName("securityData"));
        formatter.AppendElement();
        formatter.SetElement(Name.GetName("security"), "IBM US Equity");
        formatter.PushElement(Name.GetName("fieldData"));
        setFields(formatter);
        return responseEvent.First()
            .GetElement(Name.GetName("securityData"))
            .GetValueAsElement(0)
            .GetElement(Name.GetName("fieldData"));
    }

    private static Event OneMessage(
        Event.EventType eventType,
        SchemaElementDefinition definition,
        string messageXml,
        CorrelationID? correlationId)
    {
        var oneEvent = TestUtil.CreateEvent(eventType);
        var properties = new MessageProperties();
        if (correlationId is not null) properties.SetCorrelationId(correlationId);
        TestUtil.AppendMessage(oneEvent, definition, properties).FormatMessageXml(messageXml);
        return oneEvent;
    }
}
