namespace Blp;

/// <summary>A service failed to open on a started session.</summary>
/// <param name="message">What failed.</param>
public sealed class SessionStartException(string message) : SessionException(message);
