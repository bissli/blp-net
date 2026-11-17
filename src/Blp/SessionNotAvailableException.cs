namespace Blp;

/// <summary>
/// No usable Bloomberg session on this host, such as a terminal that is not logged in.
/// </summary>
/// <param name="message">What failed.</param>
/// <param name="innerException">The error that caused this one, or null.</param>
public sealed class SessionNotAvailableException(string message, Exception? innerException = null)
    : SessionException(message, innerException);
