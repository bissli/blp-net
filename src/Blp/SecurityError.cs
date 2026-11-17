namespace Blp;

/// <summary>
/// A securityError element: Bloomberg rejected one security of a request.
/// </summary>
/// <param name="Security">The security as Bloomberg echoed it.</param>
/// <param name="Category">Error category, such as BAD_SEC, or null when absent.</param>
/// <param name="Message">Bloomberg's text for the error, or null when absent.</param>
/// <param name="Subcategory">Error subcategory, or null when absent.</param>
public sealed record SecurityError(
    string Security, string? Category, string? Message, string? Subcategory);
