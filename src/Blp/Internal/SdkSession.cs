using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>An <see cref="IBlpSession"/> over a Bloomberg SDK session.</summary>
/// <param name="session">The SDK session. This object owns it.</param>
internal sealed class SdkSession(Session session) : IBlpSession
{
    /// <inheritdoc/>
    public bool Start() => session.Start();

    /// <inheritdoc/>
    public void Stop() => session.Stop();

    /// <inheritdoc/>
    public bool OpenService(string uri) => session.OpenService(uri);

    /// <inheritdoc/>
    public Service GetService(string uri) => session.GetService(uri);

    /// <inheritdoc/>
    public void SendRequest(Request request, CorrelationID correlationId) =>
        session.SendRequest(request, correlationId);

    /// <inheritdoc/>
    public void Subscribe(IList<Subscription> subscriptions) => session.Subscribe(subscriptions);

    /// <inheritdoc/>
    public void Unsubscribe(IList<Subscription> subscriptions) =>
        session.Unsubscribe(subscriptions);

    /// <inheritdoc/>
    public void BeginStop() => session.Stop(AbstractSession.StopOption.ASYNC);

    /// <inheritdoc/>
    public void Dispose() => session.Dispose();

    /// <summary>
    /// Options for a session on localhost:8194 with OS logon.
    /// </summary>
    /// <param name="applicationIdentityKey">Null connects without one.</param>
    /// <returns>The options.</returns>
    public static SessionOptions CreateOptions(string? applicationIdentityKey)
    {
        var sessionOptions = new SessionOptions
        {
            ServerHost = "localhost",
            ServerPort = 8194,
            AuthenticationOptions = "AuthenticationType=OS_LOGON",
            MaxEventQueueSize = 10000,
        };
        if (applicationIdentityKey is { } key) sessionOptions.ApplicationIdentityKey = key;
        return sessionOptions;
    }
}
