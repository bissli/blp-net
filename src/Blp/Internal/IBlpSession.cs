using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>
/// The part of a Bloomberg session that <see cref="BlpClient"/> uses.
/// </summary>
internal interface IBlpSession : IDisposable
{
    /// <summary>Starts the session.</summary>
    /// <returns>True when it started.</returns>
    bool Start();

    /// <summary>Stops the session. No event arrives after it returns.</summary>
    void Stop();

    /// <summary>Opens a service.</summary>
    /// <param name="uri">The service name, such as //blp/refdata.</param>
    /// <returns>True when it opened.</returns>
    bool OpenService(string uri);

    /// <summary>An opened service.</summary>
    /// <param name="uri">The service name, such as //blp/refdata.</param>
    /// <returns>The service.</returns>
    Service GetService(string uri);

    /// <summary>Sends a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="correlationId">The id its response events carry.</param>
    /// <exception cref="InvalidOperationException">
    /// The session is not started.
    /// </exception>
    void SendRequest(Request request, CorrelationID correlationId);
}
