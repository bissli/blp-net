namespace Blp;

/// <summary>The session failed to start.</summary>
/// <param name="message">What failed.</param>
public sealed class SessionCreateException(string message) : SessionException(message);
