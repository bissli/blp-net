namespace Blp;

/// <summary>
/// Bloomberg failed a request, with a responseError element or a RequestFailure status.
/// </summary>
/// <param name="message">The failure as Bloomberg reported it.</param>
/// <param name="category">
/// The error's category, such as LIMIT, or null when it has none.
/// </param>
/// <param name="subcategory">The error's subcategory, or null when it has none.</param>
public sealed class ResponseException(string message, string? category, string? subcategory)
    : Exception(message)
{
    /// <summary>
    /// The error's category, such as LIMIT, or null when it has none.
    /// </summary>
    public string? Category { get; } = category;

    /// <summary>The error's subcategory, or null when it has none.</summary>
    public string? Subcategory { get; } = subcategory;
}
