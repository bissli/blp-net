using System.Diagnostics.CodeAnalysis;
using Bloomberglp.Blpapi;
using Blp.Internal;
using Microsoft.Extensions.Logging;
using SdkSubscription = Bloomberglp.Blpapi.Subscription;

namespace Blp.Subscriptions;

/// <summary>
/// A live market data subscription on its own session with the local Bloomberg
/// terminal. Every member is safe from any thread.
/// </summary>
/// <remarks>
/// <see cref="Stop"/> is the only way to release the session.
/// </remarks>
public sealed partial class Subscription : IDisposable
{
    private const string MarketDataServiceName = "//blp/mktdata";

    private static readonly TimeSpan _firstDataLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _tickWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _defaultStopTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<string> _fields;
    private readonly IReadOnlyList<(string Field, Name Name)> _fieldNames;
    private readonly IReadOnlyList<string> _subscribeOptions;
    private readonly Action<string, IReadOnlyDictionary<string, object?>> _onTick;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ElementParser _parser;
    private readonly IBlpSession _session;
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _terminated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OrderedDictionary<string, TopicState> _states = [];
    private readonly Dictionary<string, Queue<long>> _tickTimes = [];
    private readonly Dictionary<string, SdkSubscription> _subscriptionByTopic = [];
    private readonly Dictionary<CorrelationID, string> _topicByCorrelationId = [];
    private long _lastCorrelationId;
    private long? _firstAddAt;
    private long? _firstDataAt;
    private string? _sessionFailure;
    private bool _connected = true;
    private int _errorCount;
    private string? _lastError;
    private bool _stopping;
    private bool _released;
    private volatile int _dispatchThreadId;

    private Subscription(
        IReadOnlyList<string> fields,
        Action<string, IReadOnlyDictionary<string, object?>> onTick,
        SubscriptionOptions options,
        Func<SessionOptions, Bloomberglp.Blpapi.EventHandler, IBlpSession> sessionFactory,
        TimeProvider timeProvider)
    {
        _fields = [.. fields];
        _fieldNames = [.. fields.Select(field => (field, Name.GetName(field.ToUpperInvariant())))];
        _subscribeOptions = options.Interval > TimeSpan.Zero
            ? [FormattableString.Invariant($"interval={options.Interval.TotalSeconds:F1}")]
            : [];
        _onTick = onTick;
        _timeProvider = timeProvider;
        _logger = options.Logger;
        _parser = new ElementParser(
            TimeZoneInfo.Local,
            forceString: false,
            timeAsDateTime: false,
            timeProvider,
            assumedTimeZone: TimeZoneInfo.Local,
            datesTimeAtLatestPast: true);
        _session = sessionFactory(
            SdkSession.CreateOptions(options.ApplicationIdentityKey),
            (sessionEvent, _) => OnEvent(sessionEvent));
        try
        {
            if (!_session.Start())
            {
                throw new SessionCreateException("Failed to start the Bloomberg session.");
            }

            if (!_session.OpenService(MarketDataServiceName))
            {
                throw new SessionStartException(
                    $"Failed to open service {MarketDataServiceName}.");
            }
        }
        catch (Exception error)
        {
            _session.Dispose();
            if (error is SessionException) throw;
            throw new SessionException("Failed to start the Bloomberg session.", error);
        }
    }

    /// <summary>
    /// Starts a session on localhost:8194 with OS logon and opens //blp/mktdata. No
    /// topic is subscribed until <see cref="Add"/>.
    /// </summary>
    /// <param name="fields">
    /// Fields whose changes raise a tick, such as "BID". Matched upper case.
    /// </param>
    /// <param name="onTick">
    /// Called with the topic and its row for each tick, on the SDK's dispatch thread,
    /// with no lock held, so it may call any member. The row holds each field present
    /// in the message, keyed as given in fields, with a date-time or time in the local
    /// zone. The subscription catches and counts what it throws.
    /// </param>
    /// <param name="options">
    /// Session and subscribe settings. Null takes the defaults.
    /// </param>
    /// <returns>The started subscription.</returns>
    /// <exception cref="SessionCreateException">
    /// The session failed to start.
    /// </exception>
    /// <exception cref="SessionStartException">
    /// //blp/mktdata failed to open.
    /// </exception>
    /// <exception cref="SessionException">The SDK threw while starting.</exception>
    public static Subscription Start(
        IReadOnlyList<string> fields,
        Action<string, IReadOnlyDictionary<string, object?>> onTick,
        SubscriptionOptions? options = null) =>
        Start(
            fields,
            onTick,
            options ?? new SubscriptionOptions(),
            (sessionOptions, handler) => new SdkSession(new Session(sessionOptions, handler)),
            TimeProvider.System);

    /// <summary>
    /// Starts a subscription on a session from sessionFactory, as the public overload.
    /// </summary>
    /// <param name="fields">Fields whose changes raise a tick.</param>
    /// <param name="onTick">Called for each tick.</param>
    /// <param name="options">Session and subscribe settings.</param>
    /// <param name="sessionFactory">
    /// Creates the session from its options and event handler.
    /// </param>
    /// <param name="timeProvider">Clock for tick times and the no-data rule.</param>
    /// <returns>The started subscription.</returns>
    internal static Subscription Start(
        IReadOnlyList<string> fields,
        Action<string, IReadOnlyDictionary<string, object?>> onTick,
        SubscriptionOptions options,
        Func<SessionOptions, Bloomberglp.Blpapi.EventHandler, IBlpSession> sessionFactory,
        TimeProvider timeProvider) =>
        new(fields, onTick, options, sessionFactory, timeProvider);

    /// <summary>Subscribes topics on the live session.</summary>
    /// <param name="topics">
    /// Topics such as "IBM US Equity". A held topic is skipped, except a failed one,
    /// which is subscribed again.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The SDK rejects a topic, such as an empty one. No topic of the call is held.
    /// </exception>
    /// <exception cref="SessionException"><see cref="Stop"/> has begun.</exception>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started. No topic of the call is held.
    /// </exception>
    public void Add(params IEnumerable<string> topics)
    {
        lock (_lock)
        {
            if (_stopping) throw new SessionException("The subscription is stopped.");
            var newTopics = topics
                .Distinct()
                .Where(topic => !_states.TryGetValue(topic, out var state)
                    || state.Status == TopicStatus.Failed)
                .ToList();
            if (newTopics.Count == 0) return;
            var priorByTopic = newTopics
                .Where(_states.ContainsKey)
                .ToDictionary(
                    topic => topic,
                    topic => (
                        Subscription: _subscriptionByTopic[topic],
                        State: _states[topic],
                        Ticks: _tickTimes[topic]));
            // The SDK constructor throws on a malformed topic, so every subscription is
            // built before any state changes.
            List<SdkSubscription> subscriptions = [
                .. newTopics.Select(topic => new SdkSubscription(
                    topic,
                    [.. _fields],
                    [.. _subscribeOptions],
                    new CorrelationID(++_lastCorrelationId))),
            ];
            foreach (var (topic, subscription) in newTopics.Zip(subscriptions))
            {
                if (_subscriptionByTopic.TryGetValue(topic, out var prior))
                {
                    _topicByCorrelationId.Remove(prior.CorrelationID);
                }

                _subscriptionByTopic[topic] = subscription;
                _topicByCorrelationId[subscription.CorrelationID] = topic;
                _states[topic] = new TopicState(topic);
                _tickTimes[topic] = new Queue<long>();
            }

            try
            {
                _session.Subscribe(subscriptions);
            }
            catch (Exception error)
            {
                foreach (var subscription in subscriptions)
                {
                    string topic = _topicByCorrelationId[subscription.CorrelationID];
                    _topicByCorrelationId.Remove(subscription.CorrelationID);
                    if (priorByTopic.TryGetValue(topic, out var prior))
                    {
                        _subscriptionByTopic[topic] = prior.Subscription;
                        _topicByCorrelationId[prior.Subscription.CorrelationID] = topic;
                        _states[topic] = prior.State;
                        _tickTimes[topic] = prior.Ticks;
                    }
                    else
                    {
                        _subscriptionByTopic.Remove(topic);
                        _states.Remove(topic);
                        _tickTimes.Remove(topic);
                    }
                }

                if (error is InvalidOperationException)
                {
                    throw new SessionNotAvailableException(error.Message, error);
                }

                throw;
            }

            _firstAddAt ??= _timeProvider.GetTimestamp();
        }
    }

    /// <summary>
    /// Unsubscribes topics and stops tracking them. A tick already being delivered
    /// may still reach onTick after Remove returns.
    /// </summary>
    /// <param name="topics">Topics to drop. A topic not held is skipped.</param>
    /// <exception cref="SessionNotAvailableException">
    /// The session is not started. The topics are dropped all the same.
    /// </exception>
    public void Remove(params IEnumerable<string> topics)
    {
        lock (_lock)
        {
            var heldTopics = topics.Distinct().Where(_states.ContainsKey).ToList();
            if (heldTopics.Count == 0) return;
            List<SdkSubscription> subscriptions = [];
            foreach (string topic in heldTopics)
            {
                var subscription = _subscriptionByTopic[topic];
                // Dropping the id first makes the unsubscribe's own
                // SubscriptionTerminated, and any late tick, unknown.
                _topicByCorrelationId.Remove(subscription.CorrelationID);
                _subscriptionByTopic.Remove(topic);
                _states.Remove(topic);
                _tickTimes.Remove(topic);
                subscriptions.Add(subscription);
            }

            if (_stopping) return;
            try
            {
                _session.Unsubscribe(subscriptions);
            }
            catch (InvalidOperationException error)
            {
                throw new SessionNotAvailableException(error.Message, error);
            }
        }
    }

    /// <summary>A consistent copy of the subscription's state, as of now.</summary>
    /// <returns>The copy. Later events leave it unchanged.</returns>
    public SubscriptionStatus GetStatus()
    {
        long now = _timeProvider.GetTimestamp();
        lock (_lock)
        {
            OrderedDictionary<string, TopicState> topics = [];
            foreach (var (topic, state) in _states)
            {
                var ticks = _tickTimes[topic];
                DropExpiredTicks(ticks, now);
                topics.Add(topic, state with { TicksLastMinute = ticks.Count });
            }

            string? failure = _sessionFailure;
            if (failure is null
                && !_stopping
                && topics.Count > 0
                && topics.Values.All(state => state.Status == TopicStatus.Failed))
            {
                failure = $"all {topics.Count} topics failed: {topics.GetAt(0).Value.Reason}";
            }

            if (failure is null && !_stopping && _firstAddAt is { } firstAddAt)
            {
                long dataAt = _firstDataAt ?? now;
                if (_timeProvider.GetElapsedTime(firstAddAt, dataAt) >= _firstDataLimit)
                {
                    failure = $"no subscription data within {_firstDataLimit.TotalSeconds} s";
                }
            }

            return new SubscriptionStatus(failure, _connected, _errorCount, _lastError, topics);
        }
    }

    /// <summary>Unsubscribes every topic and releases the session.</summary>
    /// <param name="timeout">
    /// Longest wait for the session to terminate. Null waits 10 seconds, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without limit.
    /// </param>
    /// <returns>
    /// True when the session terminated and is released. False when the wait timed out,
    /// or when called from the tick callback, which begins the stop and returns at
    /// once. A later call waits again. <see cref="GetStatus"/> still reads the last
    /// state.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// timeout is negative and not <see cref="Timeout.InfiniteTimeSpan"/>. The stop has
    /// not begun.
    /// </exception>
    public bool Stop(TimeSpan? timeout = null)
    {
        var wait = timeout ?? _defaultStopTimeout;
        if (wait < TimeSpan.Zero && wait != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Negative timeout.");
        }

        List<Exception> errors = [];
        lock (_lock)
        {
            if (!_stopping)
            {
                _stopping = true;
                var held = _subscriptionByTopic.Values.ToList();
                _topicByCorrelationId.Clear();
                try
                {
                    if (held.Count > 0) _session.Unsubscribe(held);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }

                try
                {
                    _session.BeginStop();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }

        foreach (var error in errors)
        {
            RecordError("", error);
        }

        if (Environment.CurrentManagedThreadId == _dispatchThreadId) return false;
        if (!_terminated.Task.Wait(wait))
        {
            LogStopTimedOut(_logger, wait);
            return false;
        }

        lock (_lock)
        {
            if (_released) return true;
            _released = true;
        }

        _session.Dispose();
        return true;
    }

    /// <summary>Stops as <see cref="Stop"/> does with its default timeout.</summary>
    public void Dispose() => Stop();

    private void DropExpiredTicks(Queue<long> ticks, long now)
    {
        while (ticks.Count > 0 && _timeProvider.GetElapsedTime(ticks.Peek(), now) >= _tickWindow)
        {
            ticks.Dequeue();
        }
    }

    // The SDK's event handler, on its dispatch thread. An exception left to escape it
    // would reach the SDK's dispatch thread.
    private void OnEvent(Event sessionEvent)
    {
        try
        {
            _dispatchThreadId = Environment.CurrentManagedThreadId;
            switch (sessionEvent.Type)
            {
                case Event.EventType.SUBSCRIPTION_DATA:
                    OnDataEvent(sessionEvent);
                    break;
                case Event.EventType.SUBSCRIPTION_STATUS:
                    OnStatusEvent(sessionEvent);
                    break;
                case Event.EventType.TIMEOUT:
                    break;
                default:
                    OnOtherEvent(sessionEvent);
                    break;
            }
        }
        catch (Exception error)
        {
            RecordError("", error);
        }
    }

    private void OnDataEvent(Event dataEvent)
    {
        foreach (var message in dataEvent)
        {
            long nowTimestamp = _timeProvider.GetTimestamp();
            var now = _timeProvider.GetUtcNow();
            string? topic;
            lock (_lock)
            {
                if (!TryTopicOf(message, out topic)) continue;
                var state = _states[topic];
                _states[topic] = state with
                {
                    FirstDataAt = state.FirstDataAt ?? now,
                    LastTickAt = now,
                };
                var ticks = _tickTimes[topic];
                ticks.Enqueue(nowTimestamp);
                DropExpiredTicks(ticks, nowTimestamp);
                _firstDataAt ??= nowTimestamp;
            }

            try
            {
                Dictionary<string, object?> row = [];
                foreach (var (field, name) in _fieldNames)
                {
                    if (message.HasElement(name))
                    {
                        row[field] = _parser.SubelementValue(message.AsElement, name);
                    }
                }

                _onTick(topic, row);
            }
            catch (Exception error)
            {
                RecordError(topic, error);
            }
        }
    }

    private void OnStatusEvent(Event statusEvent)
    {
        foreach (var message in statusEvent)
        {
            var messageType = message.MessageType;
            bool started = messageType.Equals(ElementNames.SubscriptionStarted);
            if (!started
                && !messageType.Equals(ElementNames.SubscriptionFailure)
                && !messageType.Equals(ElementNames.SubscriptionTerminated))
            {
                continue;
            }

            string? reason = null;
            if (!started)
            {
                var reasonElement = message.GetElement(ElementNames.Reason);
                reason = ElementParser.OptionalString(reasonElement, ElementNames.Description)
                    ?? "no description";
            }

            var now = _timeProvider.GetUtcNow();
            string? topic;
            lock (_lock)
            {
                if (!TryTopicOf(message, out topic)) continue;
                var state = _states[topic];
                _states[topic] = started
                    ? state with { Status = TopicStatus.Subscribed, SubscribedAt = now }
                    : state with { Status = TopicStatus.Failed, Reason = reason };
            }

            if (!started) LogTopicFailed(_logger, messageType, topic, reason);
        }
    }

    private void OnOtherEvent(Event otherEvent)
    {
        foreach (var message in otherEvent)
        {
            var messageType = message.MessageType;
            if (messageType.Equals(ElementNames.SessionTerminated))
            {
                lock (_lock)
                {
                    if (!_stopping) _sessionFailure = "session terminated";
                }

                // No event follows SessionTerminated, and the dispatch thread's id may
                // be reused once it exits.
                _dispatchThreadId = 0;
                _terminated.TrySetResult();
                LogSessionTerminated(_logger);
            }
            else if (messageType.Equals(ElementNames.SessionConnectionDown))
            {
                lock (_lock)
                {
                    _connected = false;
                }

                LogConnectionDown(_logger);
            }
            else if (messageType.Equals(ElementNames.SessionConnectionUp))
            {
                lock (_lock)
                {
                    _connected = true;
                }

                LogConnectionUp(_logger);
            }
            else if (messageType.Equals(ElementNames.SlowConsumerWarning))
            {
                LogSlowConsumer(_logger);
            }
            else if (messageType.Equals(ElementNames.SlowConsumerWarningCleared))
            {
                LogSlowConsumerCleared(_logger);
            }
            else if (messageType.Equals(ElementNames.DataLoss))
            {
                LogDataLoss(_logger, message.ToString());
            }
        }
    }

    private bool TryTopicOf(Message message, [NotNullWhen(true)] out string? topic)
    {
        topic = null;
        return message.CorrelationID is { } correlationId
            && _topicByCorrelationId.TryGetValue(correlationId, out topic);
    }

    // Only the first error is logged with its stack trace, since a broken onTick fails
    // on every tick.
    private void RecordError(string topic, Exception error)
    {
        string errorText = $"{error.GetType().Name}: {error.Message}";
        bool first;
        lock (_lock)
        {
            _errorCount++;
            _lastError = $"{topic}: {errorText}";
            first = _errorCount == 1;
        }

        if (first)
        {
            LogFirstError(_logger, topic, error);
        }
        else
        {
            LogLaterError(_logger, topic, errorText);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Subscription {MessageType} topic={Topic} desc={Description}")]
    private static partial void LogTopicFailed(
        ILogger logger, Name messageType, string topic, string? description);

    [LoggerMessage(
        EventId = 2, Level = LogLevel.Information, Message = "Bloomberg session terminated")]
    private static partial void LogSessionTerminated(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Bloomberg connection down")]
    private static partial void LogConnectionDown(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Bloomberg connection up")]
    private static partial void LogConnectionUp(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Event queue nearly full; ticks may be dropped")]
    private static partial void LogSlowConsumer(ILogger logger);

    [LoggerMessage(
        EventId = 6, Level = LogLevel.Warning, Message = "Event queue no longer nearly full")]
    private static partial void LogSlowConsumerCleared(ILogger logger);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Event queue overflowed, data lost: {DataLoss}")]
    private static partial void LogDataLoss(ILogger logger, string dataLoss);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Subscription error on {Topic}")]
    private static partial void LogFirstError(ILogger logger, string topic, Exception error);

    [LoggerMessage(
        EventId = 9, Level = LogLevel.Debug, Message = "Subscription error on {Topic}: {Error}")]
    private static partial void LogLaterError(ILogger logger, string topic, string error);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Bloomberg session did not stop within {Timeout}")]
    private static partial void LogStopTimedOut(ILogger logger, TimeSpan timeout);
}
