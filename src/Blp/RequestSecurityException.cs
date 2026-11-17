namespace Blp;

/// <summary>
/// A request with <c>RaiseSecurityError</c> set received a securityError element.
/// </summary>
/// <param name="errors">Every security error the response held.</param>
public sealed class RequestSecurityException(IReadOnlyList<SecurityError> errors)
    : Exception(string.Join("; ", errors))
{
    /// <summary>The securities Bloomberg rejected, in response order.</summary>
    public IReadOnlyList<SecurityError> Errors { get; } = errors;
}
