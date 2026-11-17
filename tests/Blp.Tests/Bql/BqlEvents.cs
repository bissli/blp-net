using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;

namespace Blp.Tests.Bql;

/// <summary>Builds //blp/bqlsvc result events from JSON, with no terminal.</summary>
internal static class BqlEvents
{
    private const string Schema = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ServiceDefinition name="" version="">
          <service name="//blp/bqlsvc" version="1.0.0.41">
            <operation name="sendQuery" serviceId="1073933486">
              <request>request</request>
              <requestSelection>query</requestSelection>
              <response>response</response>
              <responseSelection>result</responseSelection>
            </operation>
          </service>
          <schema>
            <sequenceType name="QueryRequest">
              <element name="expression" type="String"/>
              <element name="appName" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="clientContext" type="ClientContextType" minOccurs="0" maxOccurs="1"/>
              <element name="bqlRequestId" type="String" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <sequenceType name="ClientContextType">
              <element name="appName" type="String"/>
              <element name="clientRequestId" type="String" minOccurs="0" maxOccurs="1"/>
              <element name="timestamp" type="Int64" minOccurs="0" maxOccurs="1"/>
              <element name="extraMarkers" type="KeyValueType" maxOccurs="unbounded"/>
            </sequenceType>
            <sequenceType name="KeyValueType">
              <element name="key" type="String"/>
              <element name="value" type="String"/>
            </sequenceType>
            <choiceType name="request">
              <element name="query" id="0" type="QueryRequest"/>
            </choiceType>
            <choiceType name="response">
              <!-- The formatter cannot fill a scalar root, so the test schema gives
                   result maxOccurs="unbounded". The message then holds one string. -->
              <element name="result" id="0" type="String" maxOccurs="unbounded"/>
            </choiceType>
          </schema>
        </ServiceDefinition>
        """;

    private static readonly Service _service = RefDataEvents.DeserializeService(Schema);

    /// <summary>The deserialized //blp/bqlsvc service.</summary>
    public static Service Service => _service;

    /// <summary>An event holding one result message.</summary>
    /// <param name="json">The JSON text the message carries.</param>
    /// <param name="eventType">PARTIAL_RESPONSE or RESPONSE.</param>
    /// <param name="correlationId">The id the message answers. Null sets none.</param>
    /// <returns>The event.</returns>
    public static Event Result(
        string json,
        Event.EventType eventType = Event.EventType.RESPONSE,
        CorrelationID? correlationId = null)
    {
        var definition = _service.GetOperation(Name.GetName("sendQuery")).GetResponseDefinition(0);
        var resultEvent = TestUtil.CreateEvent(eventType);
        var properties = new MessageProperties();
        if (correlationId is not null) properties.SetCorrelationId(correlationId);
        TestUtil.AppendMessage(resultEvent, definition, properties).AppendValue(json);
        return resultEvent;
    }
}
