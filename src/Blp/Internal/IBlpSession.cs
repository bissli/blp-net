using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>
/// The part of a Bloomberg session that <see cref="BlpClient"/> and
/// <see cref="Subscriptions.Subscription"/> use.
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

    /// <summary>
    /// Subscribes topics. Each answers with a SUBSCRIPTION_STATUS event.
    /// </summary>
    /// <param name="subscriptions">The topics, each with its correlation id.</param>
    /// <exception cref="InvalidOperationException">
    /// The session is not started. No topic of the list is subscribed.
    /// </exception>
    void Subscribe(IList<Subscription> subscriptions);

    /// <summary>Cancels subscriptions by their correlation ids.</summary>
    /// <param name="subscriptions">The subscriptions to cancel.</param>
    /// <exception cref="InvalidOperationException">
    /// The session is not started.
    /// </exception>
    void Unsubscribe(IList<Subscription> subscriptions);

    /// <summary>
    /// Begins stopping the session and returns at once. SessionTerminated follows.
    /// </summary>
    void BeginStop();
}
