using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;

namespace Blp.Tests.Subscriptions;

/// <summary>Bloomberg TestUtil events for subscription tests.</summary>
internal static class MarketDataEvents
{
    // BID is a choice, which the parser cannot read, so a tick holding BID raises a
    // parse error.
    private const string Schema = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ServiceDefinition name="blp.mktdata" version="1.0.0.0">
          <service name="//blp/mktdata" version="1.0.0.0">
            <event name="MarketDataEvents" eventType="MarketDataUpdate">
              <eventId>0</eventId>
            </event>
            <defaultServiceId>1</defaultServiceId>
          </service>
          <schema>
            <sequenceType name="MarketDataUpdate">
              <element name="LAST_PRICE" type="Float64" minOccurs="0" maxOccurs="1"/>
              <element name="BID" type="BidChoice" minOccurs="0" maxOccurs="1"/>
            </sequenceType>
            <choiceType name="BidChoice">
              <element name="price" type="Float64"/>
            </choiceType>
          </schema>
        </ServiceDefinition>
        """;

    private static readonly SchemaElementDefinition _marketDataEvent = RefDataEvents
        .DeserializeService(Schema)
        .GetEventDefinition(Name.GetName("MarketDataEvents"));

    /// <summary>A SUBSCRIPTION_DATA event with one message per tick.</summary>
    /// <param name="ticks">
    /// The correlation id and the message body XML, children of MarketDataEvents.
    /// </param>
    /// <returns>The event.</returns>
    public static Event Data(params (CorrelationID Id, string BodyXml)[] ticks)
    {
        var dataEvent = TestUtil.CreateEvent(Event.EventType.SUBSCRIPTION_DATA);
        foreach (var (id, bodyXml) in ticks)
        {
            var properties = new MessageProperties();
            properties.SetCorrelationId(id);
            TestUtil.AppendMessage(dataEvent, _marketDataEvent, properties)
                .FormatMessageXml($"<MarketDataEvents>{bodyXml}</MarketDataEvents>");
        }

        return dataEvent;
    }

    /// <summary>A SubscriptionStarted message for <see cref="Status"/>.</summary>
    /// <param name="id">The subscription's correlation id.</param>
    /// <returns>The message.</returns>
    public static (CorrelationID Id, string Xml) Started(CorrelationID id) => (
        id,
        """
        <SubscriptionStarted>
          <streamIds>1</streamIds>
          <receivedFrom><address>localhost:8194</address></receivedFrom>
        </SubscriptionStarted>
        """);

    /// <summary>
    /// A SubscriptionFailure or Terminated message for <see cref="Status"/>.
    /// </summary>
    /// <param name="id">The subscription's correlation id.</param>
    /// <param name="description">The reason's description. Null leaves none.</param>
    /// <param name="messageType">SubscriptionFailure or SubscriptionTerminated.</param>
    /// <returns>The message.</returns>
    public static (CorrelationID Id, string Xml) Failed(
        CorrelationID id, string? description, string messageType = "SubscriptionFailure")
    {
        string reason = description is null ? "" : $"<description>{description}</description>";
        return (id, $"<{messageType}><reason>{reason}</reason></{messageType}>");
    }

    /// <summary>A SUBSCRIPTION_STATUS event holding the messages, in order.</summary>
    /// <param name="messages">
    /// From <see cref="Started"/> and <see cref="Failed"/>.
    /// </param>
    /// <returns>The event.</returns>
    public static Event Status(params (CorrelationID Id, string Xml)[] messages)
    {
        var statusEvent = TestUtil.CreateEvent(Event.EventType.SUBSCRIPTION_STATUS);
        foreach (var (id, xml) in messages)
        {
            string messageType = System.Xml.Linq.XElement.Parse(xml).Name.LocalName;
            var properties = new MessageProperties();
            properties.SetCorrelationId(id);
            TestUtil.AppendMessage(
                    statusEvent,
                    TestUtil.GetAdminMessageDefinition(Name.GetName(messageType)),
                    properties)
                .FormatMessageXml(xml);
        }

        return statusEvent;
    }

    /// <summary>
    /// A SESSION_STATUS event with one message and no correlation id.
    /// </summary>
    /// <param name="messageType">
    /// SessionTerminated, SessionConnectionDown or SessionConnectionUp.
    /// </param>
    /// <returns>The event.</returns>
    public static Event Session(string messageType) => RefDataEvents.Admin(
        Event.EventType.SESSION_STATUS,
        messageType == "SessionTerminated"
            ? "<SessionTerminated/>"
            : $"<{messageType}><server>localhost:8194</server></{messageType}>");
}
