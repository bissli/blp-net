namespace Blp;

/// <summary>The Bloomberg session failed, or cannot serve a request.</summary>
/// <param name="message">What failed.</param>
/// <param name="innerException">The error that caused this one, or null.</param>
public class SessionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
