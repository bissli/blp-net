using Bloomberglp.Blpapi;
using Blp.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Blp.Tests.Internal;

public class RequestDispatcherTests
{
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new();
    private readonly RequestDispatcher _dispatcher;

    public RequestDispatcherTests() => _dispatcher = new RequestDispatcher(_idleTimeout, _clock);

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
