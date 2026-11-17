namespace Blp;

/// <summary>
/// Bloomberg sent nothing for a request within
/// <see cref="BlpClientOptions.IdleTimeout"/>.
/// </summary>
/// <param name="message">The request and the limit it passed.</param>
public sealed class RequestTimeoutException(string message) : SessionException(message);
