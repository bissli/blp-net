namespace Blp;

/// <summary>
/// A request with <c>RaiseFieldError</c> set received a fieldExceptions entry.
/// </summary>
/// <param name="errors">Every field error the response held.</param>
public sealed class RequestFieldException(IReadOnlyList<FieldError> errors)
    : Exception(string.Join("; ", errors))
{
    /// <summary>The fields Bloomberg rejected, in response order.</summary>
    public IReadOnlyList<FieldError> Errors { get; } = errors;
}
