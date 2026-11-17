using Bloomberglp.Blpapi;
using Blp.Bql;
using Blp.Internal;
using Blp.Tests.Bql;

namespace Blp.Tests;

public class BlpClientTests
{
    /// <summary>A failed connectivity check stops and disposes its session.</summary>
    /// <remarks>
    /// Mutation: the client left undisposed when the check throws, which leaks the
    /// session.
    /// Oracle: one stop and one dispose, the expected counts for a session whose
    /// sendRequest fails, recorded by a recording session.
    /// </remarks>
    [Fact]
    public async Task FailedConnectivityCheckStopsOwnSession()
    {
        var session = new RecordingSession { FailSend = true };

        var error = await Assert.ThrowsAsync<SessionException>(
            () => BlpClient.ConnectAsync(
                new BlpClientOptions(), (_, _) => session, TestContext.Current.CancellationToken));

        Assert.Equal("Connectivity check failed.", error.Message);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>A session that fails to start is disposed.</summary>
    /// <remarks>
    /// Mutation: the dispose call dropped before SessionCreateException.
    /// Oracle: one dispose, the expected count for a session whose Start returns
    /// false, recorded by a recording session.
    /// </remarks>
    [Fact]
    public async Task FailedStartDisposesTheSession()
    {
        var session = new RecordingSession { StartResult = false };

        await Assert.ThrowsAsync<SessionCreateException>(
            () => BlpClient.ConnectAsync(
                new BlpClientOptions(), (_, _) => session, TestContext.Current.CancellationToken));

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>
    /// Connecting opens only //blp/refdata. A BQL call that cannot open //blp/bqlsvc
    /// throws SessionStartException.
    /// </summary>
    /// <remarks>
    /// Mutation: //blp/bqlsvc opened in ConnectAsync, which fails the connect, or a
    /// failed open swallowed in GetBqlAsync.
    /// Oracle: a recording session whose //blp/bqlsvc refuses to open, and the names
    /// it was asked to open.
    /// </remarks>
    [Fact]
    public async Task ConnectSkipsBqlServiceAndBqlCallFailsToOpenIt()
    {
        var session = new RecordingSession { BqlOpens = false };
        using var client = await Connect(session);
        Assert.Equal(["//blp/refdata"], session.OpenedServices);

        var error = await Assert.ThrowsAsync<SessionStartException>(
            () => client.GetBqlAsync(
                new BqlRequest("get(px_last) for(['IBM US Equity'])"),
                TestContext.Current.CancellationToken));

        Assert.Equal("Failed to open service //blp/bqlsvc.", error.Message);
    }

    /// <summary>
    /// After a failed open, a later BQL call opens //blp/bqlsvc, sends on it, and keeps
    /// the service for the call after that.
    /// </summary>
    /// <remarks>
    /// Mutation: the failure cached so every call fails, the request created on the
    /// refdata service, or the service opened again on each call.
    /// Oracle: a recording session that refuses the first open, then allows it, and
    /// the operation of each request it received.
    /// </remarks>
    [Fact]
    public async Task BqlServiceOpensOnFirstSuccessfulCallAndIsKept()
    {
        var session = new RecordingSession { BqlOpens = false };
        using var client = await Connect(session);
        var request = new BqlRequest("get(px_last) for(['IBM US Equity'])");
        await Assert.ThrowsAsync<SessionStartException>(
            () => client.GetBqlAsync(request, TestContext.Current.CancellationToken));
        session.BqlOpens = true;

        var response = await client.GetBqlAsync(request, TestContext.Current.CancellationToken);
        await client.GetBqlAsync(request, TestContext.Current.CancellationToken);

        var row = Assert.Single(response.Results["px_last"]);
        Assert.Equal(232.55, row["px_last"]);
        Assert.Equal(
            ["ReferenceDataRequest", "sendQuery", "sendQuery"], session.RequestOperations);
        Assert.Equal(
            ["//blp/refdata", "//blp/bqlsvc", "//blp/bqlsvc"], session.OpenedServices);
    }

    private static Task<BlpClient> Connect(RecordingSession session) =>
        BlpClient.ConnectAsync(
            new BlpClientOptions(),
            (_, handler) =>
            {
                session.Handler = handler;
                return session;
            },
            TestContext.Current.CancellationToken);

    private sealed class RecordingSession : IBlpSession
    {
        public bool StartResult { get; init; } = true;
        public bool FailSend { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool BqlOpens { get; set; } = true;
        public Bloomberglp.Blpapi.EventHandler? Handler { get; set; }
        public List<string> OpenedServices { get; } = [];
        public List<string> RequestOperations { get; } = [];

        public bool Start() => StartResult;

        public bool OpenService(string uri)
        {
            OpenedServices.Add(uri);
            return uri != "//blp/bqlsvc" || BqlOpens;
        }

        public Service GetService(string uri) =>
            uri == "//blp/bqlsvc" ? BqlEvents.Service : RefDataEvents.Service;

        public void SendRequest(Request request, CorrelationID correlationId)
        {
            if (FailSend) throw new InvalidOperationException("down");
            string operation = request.Operation.Name.ToString();
            RequestOperations.Add(operation);
            var response = operation == "sendQuery"
                ? BqlEvents.Result(
                    """
                    {"results":{"px_last":{
                      "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity"]},
                      "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[232.55]},
                      "secondaryColumns":[]}}}
                    """,
                    Event.EventType.RESPONSE,
                    correlationId)
                : RefDataEvents.Response(
                    "ReferenceDataRequest",
                    Event.EventType.RESPONSE,
                    """
                    <Response>
                      <securityData>
                        <security>IBM US Equity</security>
                        <fieldData><PX_LAST>7283.5</PX_LAST></fieldData>
                      </securityData>
                    </Response>
                    """,
                    correlationId);
            Handler?.Invoke(response, null!);
        }

        public void Stop() => StopCount++;

        public void Subscribe(IList<Subscription> subscriptions)
        {
        }

        public void Unsubscribe(IList<Subscription> subscriptions)
        {
        }

        public void BeginStop()
        {
        }

        public void Dispose() => DisposeCount++;
    }
}
