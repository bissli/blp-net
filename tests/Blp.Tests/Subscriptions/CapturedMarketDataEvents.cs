using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;

namespace Blp.Tests.Subscriptions;

/// <summary>
/// Builds //blp/mktdata subscription data events on the shape of the real service, with
/// no terminal.
/// </summary>
/// <remarks>
/// The schema is an excerpt of the service definition the SDK reads from a terminal:
/// the MarketDataUpdate elements the tests read, with the real ids and types, and the
/// enumerators they use.
/// </remarks>
internal static class CapturedMarketDataEvents
{
    private const string Schema = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ServiceDefinition name="blp.mktdata" version="1.0.1.0">
          <service name="//blp/mktdata" version="1.0.1.0">
            <event name="MarketDataEvents" eventType="MarketDataUpdate">
              <eventId>0</eventId>
            </event>
            <defaultServiceId>12291</defaultServiceId>
          </service>
          <schema>
            <sequenceType name="MarketDataUpdate">
              <element name="LAST_PRICE" id="1" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="BID" id="2" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="ASK" id="3" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="VOLUME" id="7" type="Int64" minOccurs="0" maxOccurs="1"/>
              <element name="LAST_TRADE" id="39" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="TIME" id="1023" type="Datetime" minOccurs="0" maxOccurs="1"/>
              <element name="MKTDATA_EVENT_TYPE" id="61166" type="MktdataEventType"
                minOccurs="0" maxOccurs="1"/>
              <element name="MKTDATA_EVENT_SUBTYPE" id="61167" type="MktdataEventSubType"
                minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <enumerationType name="MktdataEventType" type="String">
              <enumerator name="SUMMARY"><value><String>SUMMARY</String></value></enumerator>
              <enumerator name="TRADE"><value><String>TRADE</String></value></enumerator>
              <enumerator name="QUOTE"><value><String>QUOTE</String></value></enumerator>
            </enumerationType>
            <enumerationType name="MktdataEventSubType" type="String">
              <enumerator name="NEW"><value><String>NEW</String></value></enumerator>
              <enumerator name="BID"><value><String>BID</String></value></enumerator>
              <enumerator name="INITPAINT"><value><String>INITPAINT</String></value></enumerator>
              <enumerator name="INTERVAL"><value><String>INTERVAL</String></value></enumerator>
            </enumerationType>
          </schema>
        </ServiceDefinition>
        """;

    private static readonly SchemaElementDefinition _marketDataEvent = RefDataEvents
        .DeserializeService(Schema)
        .GetEventDefinition(Name.GetName("MarketDataEvents"));

    /// <summary>A SUBSCRIPTION_DATA event with one message per tick.</summary>
    /// <param name="ticks">
    /// The correlation id and the call that sets the message's elements, at its root.
    /// </param>
    /// <returns>The event.</returns>
    public static Event Data(
        params (CorrelationID Id, Action<IMessageFormatter> SetElements)[] ticks)
    {
        var dataEvent = TestUtil.CreateEvent(Event.EventType.SUBSCRIPTION_DATA);
        foreach (var (id, setElements) in ticks)
        {
            var properties = new MessageProperties();
            properties.SetCorrelationId(id);
            setElements(TestUtil.AppendMessage(dataEvent, _marketDataEvent, properties));
        }

        return dataEvent;
    }
}
