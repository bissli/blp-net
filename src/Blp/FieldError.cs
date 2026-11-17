namespace Blp;

/// <summary>
/// A fieldExceptions entry: Bloomberg rejected one field of one security.
/// </summary>
/// <param name="Security">The security as Bloomberg echoed it.</param>
/// <param name="Field">The rejected field mnemonic.</param>
/// <param name="Category">Error category, such as BAD_FLD, or null when absent.</param>
/// <param name="Message">Bloomberg's text for the error, or null when absent.</param>
/// <param name="Subcategory">Error subcategory, or null when absent.</param>
public sealed record FieldError(
    string Security, string Field, string? Category, string? Message, string? Subcategory);
