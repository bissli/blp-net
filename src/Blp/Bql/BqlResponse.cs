namespace Blp.Bql;

/// <summary>The answer to a <see cref="BqlRequest"/>.</summary>
/// <param name="Results">
/// Result name, as Bloomberg sends it, such as "px_last(dates=range(-5d,0d))", to its
/// rows, in arrival order. A row holds "ID", then the result name for the value, then
/// each secondary column by name. A null value or a "NaN" is null. A value takes its
/// column's type: double, long (INT), bool, <see cref="DateOnly"/> (DATE) or string.
/// A value that does not fit the type comes back as sent, a number as double and text
/// as string.
/// </param>
/// <param name="Errors">The message of each exception Bloomberg reported.</param>
public sealed record BqlResponse(
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Results,
    IReadOnlyList<string> Errors);
