using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>
/// Builds one request and collects its response events.
/// </summary>
internal interface IRequestHandler
{
    /// <summary>The Bloomberg request to send.</summary>
    /// <param name="service">The opened service the request belongs to.</param>
    /// <returns>A filled-in request.</returns>
    Request CreateRequest(Service service);

    /// <summary>Collects the data in one PARTIAL_RESPONSE or RESPONSE event.</summary>
    /// <param name="responseEvent">An event whose messages answer this request.</param>
    /// <exception cref="ResponseException">
    /// A message holds a responseError element.
    /// </exception>
    void ProcessResponse(Event responseEvent);
}
