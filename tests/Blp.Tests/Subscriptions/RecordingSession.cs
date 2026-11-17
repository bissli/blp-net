using Bloomberglp.Blpapi;
using Blp.Internal;
using SdkSubscription = Bloomberglp.Blpapi.Subscription;

namespace Blp.Tests.Subscriptions;

/// <summary>
/// An <see cref="IBlpSession"/> that records subscribe and stop calls.
/// </summary>
internal sealed class RecordingSession : IBlpSession
{
    /// <summary>Thrown by OpenService, or null to open.</summary>
    public Exception? OpenServiceError { get; init; }

    /// <summary>Thrown by each Subscribe call in turn. A null entry succeeds.</summary>
    public Queue<Exception?> SubscribeErrors { get; } = [];

    /// <summary>Thrown by Unsubscribe, or null to succeed.</summary>
    public Exception? UnsubscribeError { get; set; }

    /// <summary>Run by BeginStop, such as delivering SessionTerminated.</summary>
    public Action? OnBeginStop { get; set; }

    /// <summary>The list of each successful Subscribe call.</summary>
    public List<List<SdkSubscription>> SubscribeCalls { get; } = [];

    /// <summary>The list of each Unsubscribe call.</summary>
    public List<List<SdkSubscription>> UnsubscribeCalls { get; } = [];

    /// <summary>Subscribe calls, failed ones included.</summary>
    public int SubscribeAttempts { get; private set; }

    /// <summary>BeginStop calls.</summary>
    public int BeginStopCount { get; private set; }

    /// <summary>Dispose calls.</summary>
    public int DisposeCount { get; private set; }

    public bool Start() => true;

    public bool OpenService(string uri) => OpenServiceError is null ? true : throw OpenServiceError;

    public Service GetService(string uri) => throw new NotSupportedException();

    public void SendRequest(Request request, CorrelationID correlationId) =>
        throw new NotSupportedException();

    public void Stop() => throw new NotSupportedException();

    public void Subscribe(IList<SdkSubscription> subscriptions)
    {
        SubscribeAttempts++;
        if (SubscribeErrors.TryDequeue(out var error) && error is not null) throw error;
        SubscribeCalls.Add([.. subscriptions]);
    }

    public void Unsubscribe(IList<SdkSubscription> subscriptions)
    {
        UnsubscribeCalls.Add([.. subscriptions]);
        if (UnsubscribeError is not null) throw UnsubscribeError;
    }

    public void BeginStop()
    {
        BeginStopCount++;
        OnBeginStop?.Invoke();
    }

    public void Dispose() => DisposeCount++;
}
