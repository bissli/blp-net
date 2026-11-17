using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bloomberglp.Blpapi;

namespace CaptureFixtures;

/// <summary>Sends requests and subscriptions on a session and records events.</summary>
/// <param name="session">A started session without an event handler.</param>
internal sealed class SessionRecorder(Session session)
{
    private const string MarketDataService = "//blp/mktdata";
    private const int PollMilliseconds = 500;
    private static readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _unsubscribeDrain = TimeSpan.FromSeconds(5);

    /// <summary>Appends the events that arrive within a time span.</summary>
    /// <param name="events">Receives one record per event.</param>
    /// <param name="duration">How long to poll.</param>
    public void Collect(JsonArray events, TimeSpan duration)
    {
        foreach (var received in Poll(duration)) events.Add(EventRecord(received));
    }

    /// <summary>Sends a case's request and records events to the final one.</summary>
    /// <param name="captureCase">The request to send.</param>
    /// <param name="correlationId">Unique within the session.</param>
    /// <param name="record">
    /// Receives <c>request</c> before the send and <c>events</c> as they arrive, so a
    /// failure leaves what was captured.
    /// </param>
    /// <exception cref="TimeoutException">
    /// No RESPONSE or REQUEST_STATUS for the request arrived within 120 seconds.
    /// </exception>
    public void CaptureRequest(CaptureCase captureCase, long correlationId, JsonObject record)
    {
        OpenService(captureCase.Service);
        var request = session.GetService(captureCase.Service).CreateRequest(captureCase.Operation);
        captureCase.Fill(request);
        using var requestText = new StringWriter();
        request.Print(requestText);
        record["request"] = requestText.ToString();
        var events = new JsonArray();
        record["events"] = events;

        var id = new CorrelationID(correlationId);
        session.SendRequest(request, id);
        foreach (var received in Poll(_responseTimeout))
        {
            events.Add(EventRecord(received));
            bool isFinal =
                received.Type is Event.EventType.RESPONSE or Event.EventType.REQUEST_STATUS;
            bool isOurs = received.GetMessages()
                .Any(message => message.CorrelationIDs.Contains(id));
            if (isFinal && isOurs) return;
        }

        // A late event of an abandoned request would land in the next case.
        session.Cancel(id);
        throw new TimeoutException($"No final response in {_responseTimeout.TotalSeconds} s");
    }

    /// <summary>Subscribes the topics, records events, then unsubscribes.</summary>
    /// <param name="options">Subscription options such as <c>interval=5.0</c>.</param>
    /// <param name="firstCorrelationId">
    /// Correlation id of the first topic. The rest follow in order, and none may repeat
    /// an earlier capture's ids.
    /// </param>
    /// <param name="duration">How long to collect before unsubscribing.</param>
    /// <param name="record">Receives the topics, fields, options and events.</param>
    public void CaptureSubscription(
        string[] options, long firstCorrelationId, TimeSpan duration, JsonObject record)
    {
        OpenService(MarketDataService);
        var events = new JsonArray();
        record["topics"] = JsonSerializer.SerializeToNode(Cases.SubscribeTopics);
        record["fields"] = JsonSerializer.SerializeToNode(Cases.SubscribeFields);
        record["options"] = JsonSerializer.SerializeToNode(options);
        record["events"] = events;

        var subscriptions = Cases.SubscribeTopics
            .Select((topic, index) => new Subscription(
                topic,
                Cases.SubscribeFields,
                options,
                new CorrelationID(firstCorrelationId + index)))
            .ToList();
        session.Subscribe(subscriptions);
        try
        {
            Collect(events, duration);
        }
        finally
        {
            session.Unsubscribe(subscriptions);
            Collect(events, _unsubscribeDrain);
        }
    }

    private static JsonObject EventRecord(Event received) => new()
    {
        ["eventType"] = received.Type.ToString(),
        ["receivedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        ["messages"] = new JsonArray(received.GetMessages().Select(MessageRecord).ToArray()),
    };

    private static JsonObject MessageRecord(Message message) => new()
    {
        ["messageType"] = message.MessageType.ToString(),
        ["correlationIds"] =
            new JsonArray(message.CorrelationIDs.Select(CorrelationRecord).ToArray()),
        ["tree"] = ElementTree(message.AsElement),
    };

    private static JsonObject CorrelationRecord(CorrelationID? id) => new()
    {
        ["type"] = id switch
        {
            null => "NULL",
            { IsObject: true } => "OBJECT_TYPE",
            { IsInternal: true } => "AUTOGEN_TYPE",
            { IsValue: true } => "INT_TYPE",
            _ => "UNSET_TYPE",
        },
        ["value"] = id switch
        {
            { IsObject: true } => id.Object?.ToString(),
            { IsValue: true } => id.Value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        },
    };

    /// <summary>An element and its children, with each value as text.</summary>
    /// <param name="element">Any element.</param>
    /// <returns>
    /// name, datatype, isArray and isNull, then <c>values</c> for an array,
    /// <c>elements</c> for a sequence or the one chosen element of a choice (empty for
    /// a null choice), or <c>text</c> for a scalar. A date or time keeps its
    /// GetValueAsString text, which shows the parts Bloomberg set.
    /// </returns>
    private static JsonObject ElementTree(Element element)
    {
        var datatype = element.Datatype;
        var node = new JsonObject
        {
            ["name"] = element.Name.ToString(),
            ["datatype"] = datatype.ToString(),
            ["isArray"] = element.IsArray,
            ["isNull"] = element.IsNull,
        };
        bool isComplex = datatype is Schema.Datatype.SEQUENCE or Schema.Datatype.CHOICE;
        if (element.IsArray)
        {
            var values = new JsonArray();
            for (var index = 0; index < element.NumValues; index++)
            {
                if (isComplex) values.Add(ElementTree(element.GetValueAsElement(index)));
                else values.Add(element.GetValueAsString(index));
            }

            node["values"] = values;
        }
        else if (datatype == Schema.Datatype.CHOICE)
        {
            node["elements"] = element.IsNull
                ? new JsonArray()
                : new JsonArray(ElementTree(element.GetChoice()));
        }
        else if (isComplex)
        {
            node["elements"] = new JsonArray(element.Elements.Select(ElementTree).ToArray());
        }
        else if (!element.IsNull)
        {
            node["text"] = element.GetValueAsString();
        }

        return node;
    }

    private void OpenService(string uri)
    {
        if (!session.OpenService(uri)) throw new InvalidOperationException($"Failed to open {uri}");
    }

    private IEnumerable<Event> Poll(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            var received = session.NextEvent(PollMilliseconds);
            if (received.Type != Event.EventType.TIMEOUT) yield return received;
        }
    }
}
