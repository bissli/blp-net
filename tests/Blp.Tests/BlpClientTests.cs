using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.Tests;

public class BlpClientTests
{
    /// <summary>A failed connectivity check stops and disposes its session.</summary>
    /// <remarks>
    /// Mutation: the client left undisposed when the check throws, which leaks the
    /// session.
    /// Oracle: the Python blp test_failed_connectivity_test_stops_own_session expected
    /// value, one cleanup. A recording session whose sendRequest fails.
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
    /// Oracle: the Python blp test_failed_start_cleans_up_session expected value, one
    /// cleanup. A recording session whose Start returns false.
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

    private sealed class RecordingSession : IBlpSession
    {
        public bool StartResult { get; init; } = true;
        public bool FailSend { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool Start() => StartResult;

        public bool OpenService(string uri) => true;

        public Service GetService(string uri) => RefDataEvents.Service;

        public void SendRequest(Request request, CorrelationID correlationId)
        {
            if (FailSend) throw new InvalidOperationException("down");
        }

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;
    }
}
