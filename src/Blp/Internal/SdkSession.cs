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
    public void Dispose() => session.Dispose();
}
