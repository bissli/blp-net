namespace Blp;

/// <summary>The session terminated while a request waited for its response.</summary>
/// <param name="message">What failed.</param>
public sealed class SessionTerminatedException(string message) : SessionException(message);
