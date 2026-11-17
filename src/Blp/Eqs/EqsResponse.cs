namespace Blp.Eqs;

/// <summary>The answer to an <see cref="EqsRequest"/>.</summary>
/// <param name="Values">
/// Security, as Bloomberg echoed it, to column to value, for each security the screen
/// selects. The columns are the ones the screen sends.
/// </param>
/// <param name="SecurityErrors">Securities Bloomberg rejected.</param>
/// <param name="FieldErrors">Fields Bloomberg rejected, per security.</param>
public sealed record EqsResponse(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Values,
    IReadOnlyList<SecurityError> SecurityErrors,
    IReadOnlyList<FieldError> FieldErrors);
