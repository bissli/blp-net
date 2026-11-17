namespace Blp;

/// <summary>
/// Bloomberg sent nothing for a request within its idle limit, or sent no full
/// response within its total limit.
/// </summary>
/// <param name="message">The request and the limit it passed.</param>
public sealed class RequestTimeoutException(string message) : SessionException(message);
