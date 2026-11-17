using System.Collections.Concurrent;
using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>
/// Routes session events to the requests they answer, and fails a request whose next
/// event or full response does not arrive within its limits.
/// </summary>
/// <param name="idleTimeout">
/// Longest wait for a request's next event, for a request that sets none. Each partial
/// response restarts it.
/// </param>
/// <param name="totalTimeout">
/// Longest wait for a request's full response, for a request that sets none. Null sets
/// no limit.
/// </param>
/// <param name="timeProvider">Clock for the timers.</param>
internal sealed class RequestDispatcher(
    TimeSpan idleTimeout, TimeSpan? totalTimeout, TimeProvider timeProvider)
{
    private readonly TimeSpan _idleTimeout = idleTimeout;
    private readonly TimeSpan? _totalTimeout = totalTimeout;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ConcurrentDictionary<CorrelationID, PendingRequest> _pending = new();

    /// <summary>
    /// Waits for the response to correlationId, which must not be pending.
    /// </summary>
    /// <param name="correlationId">The id the request is sent with.</param>
    /// <param name="handler">Processes each response event.</param>
    /// <param name="idleTimeout">
    /// This request's idle limit. Null takes the dispatcher's.
    /// </param>
    /// <param name="totalTimeout">
    /// This request's total limit, counted from registration. Null takes the
    /// dispatcher's.
    /// </param>
    /// <returns>
    /// A task that completes once handler processes the RESPONSE event, and faults with
    /// the request's failure. Either limit passing faults it with a
    /// <see cref="RequestTimeoutException"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A limit is negative or longer than a timer can run. Nothing stays pending.
    /// </exception>
    public Task Register(
        CorrelationID correlationId,
        IRequestHandler handler,
        TimeSpan? idleTimeout = null,
        TimeSpan? totalTimeout = null)
    {
        var idleLimit = idleTimeout ?? _idleTimeout;
        var totalLimit = totalTimeout ?? _totalTimeout;
        var idleTimer = CreateTimer(
            correlationId,
            $"No data for request {correlationId} within the idle limit of {idleLimit}.");
        var totalTimer = totalLimit is null
            ? null
            : CreateTimer(
                correlationId,
                $"No full response for request {correlationId} within the total limit of "
                    + $"{totalLimit}.");
        var pending = new PendingRequest(handler, idleLimit, idleTimer, totalTimer);
        _pending[correlationId] = pending;
        try
        {
            idleTimer.Change(idleLimit, Timeout.InfiniteTimeSpan);
            totalTimer?.Change(totalLimit.GetValueOrDefault(), Timeout.InfiniteTimeSpan);
        }
        catch (ArgumentOutOfRangeException)
        {
            _pending.TryRemove(correlationId, out _);
            pending.StopTimers();
            throw;
        }

        return pending.Completion.Task;
    }

    /// <summary>Faults the pending request for correlationId, if any.</summary>
    /// <param name="correlationId">The request's id.</param>
    /// <param name="error">The exception its task faults with.</param>
    public void Fail(CorrelationID correlationId, Exception error)
    {
        if (!_pending.TryRemove(correlationId, out var pending)) return;
        pending.StopTimers();
        pending.Completion.TrySetException(error);
    }

    /// <summary>Faults every pending request.</summary>
    /// <param name="error">The exception each task faults with.</param>
    public void FailAll(Exception error)
    {
        foreach (var correlationId in _pending.Keys)
        {
            Fail(correlationId, error);
        }
    }

    /// <summary>Hands one session event to the request it answers.</summary>
    /// <param name="sessionEvent">
    /// Any event. A response or request status for no pending request is dropped, and a
    /// SessionTerminated status fails every pending request.
    /// </param>
    public void Dispatch(Event sessionEvent)
    {
        switch (sessionEvent.Type)
        {
            case Event.EventType.PARTIAL_RESPONSE:
            case Event.EventType.RESPONSE:
            case Event.EventType.REQUEST_STATUS:
                foreach (var message in sessionEvent)
                {
                    if (_pending.TryGetValue(message.CorrelationID, out var pending))
                    {
                        Route(sessionEvent, message, pending);
                        return;
                    }
                }

                break;
            case Event.EventType.SESSION_STATUS:
                if (sessionEvent.Any(
                    message => message.MessageType.Equals(ElementNames.SessionTerminated)))
                {
                    FailAll(new SessionTerminatedException("The Bloomberg session terminated."));
                }

                break;
        }
    }

    private static Exception RequestFailure(Message message)
    {
        var reason = message.HasElement(ElementNames.Reason)
            ? message.GetElement(ElementNames.Reason)
            : null;
        string? category = reason is null
            ? null
            : ElementParser.OptionalString(reason, ElementNames.Category);
        string? subcategory = reason is null
            ? null
            : ElementParser.OptionalString(reason, ElementNames.Subcategory);
        string text = $"Request failed: {message}";
        return category is "CONNECTION_DEAD" or "SERVICE_NOT_AVAILABLE"
            ? new SessionNotAvailableException(text)
            : new ResponseException(text, category, subcategory);
    }

    private void Route(Event sessionEvent, Message message, PendingRequest pending)
    {
        var correlationId = message.CorrelationID;
        if (sessionEvent.Type == Event.EventType.REQUEST_STATUS)
        {
            Fail(correlationId, RequestFailure(message));
            return;
        }

        // Left uncaught, a handler exception would reach the SDK's dispatch thread.
        try
        {
            pending.Handler.ProcessResponse(sessionEvent);
        }
        catch (Exception error)
        {
            Fail(correlationId, error);
            return;
        }

        if (sessionEvent.Type == Event.EventType.PARTIAL_RESPONSE)
        {
            pending.IdleTimer.Change(pending.IdleLimit, Timeout.InfiniteTimeSpan);
            return;
        }

        if (_pending.TryRemove(correlationId, out _))
        {
            pending.StopTimers();
            pending.Completion.TrySetResult();
        }
    }

    private ITimer CreateTimer(CorrelationID correlationId, string message) =>
        _timeProvider.CreateTimer(
            _ => Fail(correlationId, new RequestTimeoutException(message)),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

    private sealed class PendingRequest(
        IRequestHandler handler, TimeSpan idleLimit, ITimer idleTimer, ITimer? totalTimer)
    {
        public IRequestHandler Handler { get; } = handler;
        public TimeSpan IdleLimit { get; } = idleLimit;
        public ITimer IdleTimer { get; } = idleTimer;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StopTimers()
        {
            IdleTimer.Dispose();
            totalTimer?.Dispose();
        }
    }
}
