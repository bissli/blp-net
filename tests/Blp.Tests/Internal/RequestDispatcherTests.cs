using Bloomberglp.Blpapi;
using Blp.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Blp.Tests.Internal;

public class RequestDispatcherTests
{
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new();
    private readonly RequestDispatcher _dispatcher;

    public RequestDispatcherTests() =>
        _dispatcher = new RequestDispatcher(_idleTimeout, null, _clock);

    /// <summary>
    /// A partial response keeps the request open, and the response completes it.
    /// </summary>
    /// <remarks>
    /// Mutation: PARTIAL_RESPONSE treated as final, or RESPONSE not completing the
    /// task.
    /// Oracle: a stub handler that counts the events it receives.
    /// </remarks>
    [Fact]
    public void PartialThenResponseCompletes()
    {
        var id = new CorrelationID(1);
        var handler = new RecordingHandler();
        var task = _dispatcher.Register(id, handler);

        _dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        Assert.False(task.IsCompleted);
        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(2, handler.EventCount);
    }

    /// <summary>The idle timeout faults the request exactly at its limit.</summary>
    /// <remarks>
    /// Mutation: the timer armed for a different span, or never armed.
    /// Oracle: the limit and one tick either side of it.
    /// </remarks>
    [Fact]
    public void IdleTimeoutFaultsAtItsLimit()
    {
        var task = _dispatcher.Register(new CorrelationID(1), new RecordingHandler());

        _clock.Advance(_idleTimeout - TimeSpan.FromTicks(1));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromTicks(1));

        Assert.IsType<RequestTimeoutException>(task.Exception?.InnerException);
    }

    /// <summary>A partial response restarts the idle timeout.</summary>
    /// <remarks>
    /// Mutation: the timer not reset on PARTIAL_RESPONSE.
    /// Oracle: a partial at 20 s of a 30 s limit, so the old deadline passes at 30 s
    /// and the new one at 50 s.
    /// </remarks>
    [Fact]
    public void PartialResponseRestartsTheIdleTimeout()
    {
        var id = new CorrelationID(1);
        var task = _dispatcher.Register(id, new RecordingHandler());

        _clock.Advance(TimeSpan.FromSeconds(20));
        _dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.IsType<RequestTimeoutException>(task.Exception?.InnerException);
    }

    /// <summary>
    /// A response past one idle limit completes the request, after a partial restarted
    /// the timer.
    /// </summary>
    /// <remarks>
    /// Mutation: the deadline not reset on PARTIAL_RESPONSE.
    /// Oracle: the Python blp test_partial_response_restarts_idle_limit expected value.
    /// The response at 49 s is past one 30 s limit and within 30 s of the partial at
    /// 20 s.
    /// </remarks>
    [Fact]
    public void ResponseAfterRestartedIdleTimeoutCompletes()
    {
        var id = new CorrelationID(1);
        var handler = new RecordingHandler();
        var task = _dispatcher.Register(id, handler);

        _clock.Advance(TimeSpan.FromSeconds(20));
        _dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        _clock.Advance(TimeSpan.FromSeconds(29));
        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(2, handler.EventCount);
    }

    /// <summary>The total limit ends a partial stream that never goes idle.</summary>
    /// <remarks>
    /// Mutation: the total deadline dropped, or restarted on each partial.
    /// Oracle: the Python blp test_total_limit_ends_steady_partial_stream expected
    /// value. A partial every 5 s under an 8 s idle limit and a 20 s total limit: the
    /// request is open one tick before 20 s and fails at 20 s with a total-limit
    /// message.
    /// </remarks>
    [Fact]
    public void TotalLimitEndsSteadyPartialStream()
    {
        var dispatcher = new RequestDispatcher(
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), _clock);
        var id = new CorrelationID(1);
        var task = dispatcher.Register(id, new RecordingHandler());

        for (int partial = 0; partial < 3; partial++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        }

        _clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromTicks(1));

        var error = Assert.IsType<RequestTimeoutException>(task.Exception?.InnerException);
        Assert.Contains("total limit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A request's own limits replace the dispatcher's.</summary>
    /// <remarks>
    /// Mutation: the dispatcher's idle limit used for every request, or one request's
    /// idle limit applied to another.
    /// Oracle: two requests under a 30 s dispatcher idle limit. The first sets a 10 s
    /// idle limit and fails at 10 s. The second sets none and is open at 10 s.
    /// </remarks>
    [Fact]
    public void RequestLimitsReplaceTheDefaults()
    {
        var own = _dispatcher.Register(
            new CorrelationID(1), new RecordingHandler(), TimeSpan.FromSeconds(10));
        var inherited = _dispatcher.Register(new CorrelationID(2), new RecordingHandler());

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.IsType<RequestTimeoutException>(own.Exception?.InnerException);
        Assert.False(inherited.IsCompleted);
    }

    /// <summary>
    /// A request's own total limit applies where the dispatcher has none.
    /// </summary>
    /// <remarks>
    /// Mutation: the dispatcher's total limit used in place of the request's, or the
    /// request's idle limit passed as its total limit.
    /// Oracle: a partial every 5 s under an 8 s idle limit, no dispatcher total limit,
    /// and a 20 s request total limit: open one tick before 20 s, failed at 20 s.
    /// </remarks>
    [Fact]
    public void RequestTotalLimitEndsSteadyPartialStream()
    {
        var dispatcher = new RequestDispatcher(TimeSpan.FromSeconds(8), null, _clock);
        var id = new CorrelationID(1);
        var task = dispatcher.Register(
            id, new RecordingHandler(), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20));

        for (int partial = 0; partial < 3; partial++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        }

        _clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromTicks(1));

        var error = Assert.IsType<RequestTimeoutException>(task.Exception?.InnerException);
        Assert.Contains("total limit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A steady partial stream finishes, as no total limit ends it.</summary>
    /// <remarks>
    /// Mutation: a partial that does not restart the idle timeout, or a default total
    /// limit, which ends the stream before its response.
    /// Oracle: the Python blp test_no_total_limit_lets_steady_stream_finish expected
    /// value. A partial every 5 s under an 8 s idle limit, 20 times, then a response:
    /// 21 events.
    /// </remarks>
    [Fact]
    public void SteadyPartialStreamFinishesUnderIdleLimit()
    {
        var dispatcher = new RequestDispatcher(TimeSpan.FromSeconds(8), null, _clock);
        var id = new CorrelationID(1);
        var handler = new RecordingHandler();
        var task = dispatcher.Register(id, handler);

        for (int partial = 0; partial < 20; partial++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, id));
        }

        _clock.Advance(TimeSpan.FromSeconds(5));
        dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(21, handler.EventCount);
    }

    /// <summary>
    /// A partial response for another request leaves the idle limit alone.
    /// </summary>
    /// <remarks>
    /// Mutation: the timer reset before the correlation id check.
    /// Oracle: the Python blp test_stale_partial_does_not_restart_idle_limit expected
    /// value. The stale partial at 20 s of a 30 s limit changes nothing, so the limit
    /// still expires at 30 s.
    /// </remarks>
    [Fact]
    public void StalePartialDoesNotRestartTheIdleTimeout()
    {
        var task = _dispatcher.Register(new CorrelationID(1), new RecordingHandler());

        _clock.Advance(TimeSpan.FromSeconds(20));
        _dispatcher.Dispatch(Answer(Event.EventType.PARTIAL_RESPONSE, new CorrelationID(2)));
        _clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromTicks(1));

        Assert.IsType<RequestTimeoutException>(task.Exception?.InnerException);
    }

    /// <summary>
    /// A stale response is skipped, and the request's own is processed.
    /// </summary>
    /// <remarks>
    /// Mutation: the correlation id filter dropped, so the stale response ends the
    /// request or feeds its handler.
    /// Oracle: the Python blp test_stale_response_is_skipped expected value. The
    /// handler receives exactly one event.
    /// </remarks>
    [Fact]
    public void StaleResponseIsSkipped()
    {
        var id = new CorrelationID(1);
        var handler = new RecordingHandler();
        var task = _dispatcher.Register(id, handler);

        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, new CorrelationID(2)));
        Assert.False(task.IsCompleted);
        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, handler.EventCount);
    }

    /// <summary>A response for no pending request is dropped.</summary>
    /// <remarks>
    /// Mutation: the first pending request handed any response, or an exception for an
    /// unknown id.
    /// Oracle: a stub handler that counts the events it receives.
    /// </remarks>
    [Fact]
    public void UnknownCorrelationIdIsDropped()
    {
        var handler = new RecordingHandler();
        var task = _dispatcher.Register(new CorrelationID(1), handler);

        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, new CorrelationID(2)));

        Assert.False(task.IsCompleted);
        Assert.Equal(0, handler.EventCount);
    }

    /// <summary>
    /// A RequestFailure with a connection or service category means the session is
    /// unavailable. Any other category is a response failure.
    /// </summary>
    /// <remarks>
    /// Mutation: one exception type for every category, or the category dropped.
    /// Oracle: the categories the Python blp maps to a session error.
    /// </remarks>
    [Theory]
    [InlineData("CONNECTION_DEAD", typeof(SessionNotAvailableException))]
    [InlineData("SERVICE_NOT_AVAILABLE", typeof(SessionNotAvailableException))]
    [InlineData("LIMIT", typeof(ResponseException))]
    public void RequestFailureMapsByCategory(string category, Type expected)
    {
        var id = new CorrelationID(1);
        var task = _dispatcher.Register(id, new RecordingHandler());

        _dispatcher.Dispatch(RefDataEvents.Admin(
            Event.EventType.REQUEST_STATUS,
            $"""
            <RequestFailure>
              <reason>
                <source>test</source>
                <errorCode>1</errorCode>
                <category>{category}</category>
                <description>failed</description>
                <subcategory>SUB</subcategory>
              </reason>
            </RequestFailure>
            """,
            id));

        var error = task.Exception?.InnerException;
        Assert.IsType(expected, error);
        if (error is ResponseException response) Assert.Equal(category, response.Category);
    }

    /// <summary>
    /// A RequestFailure for a bad request is a response failure, which no
    /// SessionException is.
    /// </summary>
    /// <remarks>
    /// Mutation: every RequestFailure raised as a session error, which a caller would
    /// read as a lost session.
    /// Oracle: the Python blp test_bad_request_failure_is_not_session_error expected
    /// value, a BAD_ARGS failure.
    /// </remarks>
    [Fact]
    public void BadRequestFailureIsNotASessionError()
    {
        var id = new CorrelationID(1);
        var task = _dispatcher.Register(id, new RecordingHandler());

        _dispatcher.Dispatch(Failure("BAD_ARGS", id));

        var error = Assert.IsType<ResponseException>(task.Exception?.InnerException);
        Assert.Equal("BAD_ARGS", error.Category);
        Assert.IsNotType<SessionException>(error, exactMatch: false);
    }

    /// <summary>A RequestFailure for another request does not fail this one.</summary>
    /// <remarks>
    /// Mutation: a REQUEST_STATUS failure that fails the first pending request, or a
    /// CONNECTION_DEAD status that fails every request before the id check.
    /// Oracle: the Python blp test_stale_failure_is_skipped expected value. A stale
    /// CONNECTION_DEAD failure, then this request's response completes it.
    /// </remarks>
    [Fact]
    public void StaleFailureIsSkipped()
    {
        var id = new CorrelationID(1);
        var handler = new RecordingHandler();
        var task = _dispatcher.Register(id, handler);

        _dispatcher.Dispatch(Failure("CONNECTION_DEAD", new CorrelationID(2)));
        Assert.False(task.IsCompleted);
        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, handler.EventCount);
    }

    /// <summary>SessionTerminated fails every pending request.</summary>
    /// <remarks>
    /// Mutation: only the first request failed, or a SESSION_STATUS event ignored.
    /// Oracle: two pending requests.
    /// </remarks>
    [Fact]
    public void SessionTerminatedFailsEveryRequest()
    {
        var first = _dispatcher.Register(new CorrelationID(1), new RecordingHandler());
        var second = _dispatcher.Register(new CorrelationID(2), new RecordingHandler());

        _dispatcher.Dispatch(RefDataEvents.Admin(
            Event.EventType.SESSION_STATUS, "<SessionTerminated/>"));

        Assert.IsType<SessionTerminatedException>(first.Exception?.InnerException);
        Assert.IsType<SessionTerminatedException>(second.Exception?.InnerException);
    }

    /// <summary>
    /// A handler exception faults its task and never reaches Dispatch.
    /// </summary>
    /// <remarks>
    /// Mutation: the catch removed, so Dispatch throws on the SDK's thread.
    /// Oracle: a stub handler that throws.
    /// </remarks>
    [Fact]
    public void HandlerExceptionFaultsTheTask()
    {
        var id = new CorrelationID(1);
        var failure = new InvalidOperationException("bad event");
        var task = _dispatcher.Register(id, new RecordingHandler(failure));

        _dispatcher.Dispatch(Answer(Event.EventType.RESPONSE, id));

        Assert.Same(failure, task.Exception?.InnerException);
    }

    private static Event Answer(Event.EventType eventType, CorrelationID id) =>
        RefDataEvents.Response(
            "ReferenceDataRequest", eventType, "<Response><securityData/></Response>", id);

    private static Event Failure(string category, CorrelationID id) =>
        RefDataEvents.Admin(
            Event.EventType.REQUEST_STATUS,
            $"""
            <RequestFailure>
              <reason>
                <source>test</source>
                <errorCode>1</errorCode>
                <category>{category}</category>
                <description>failed</description>
              </reason>
            </RequestFailure>
            """,
            id);

    private sealed class RecordingHandler(Exception? failure = null) : IRequestHandler
    {
        public int EventCount { get; private set; }

        public Request CreateRequest(Service service) => throw new NotSupportedException();

        public void ProcessResponse(Event responseEvent)
        {
            EventCount++;
            if (failure is not null) throw failure;
        }
    }
}
