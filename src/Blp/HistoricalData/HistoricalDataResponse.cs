namespace Blp.HistoricalData;

/// <summary>The answer to a <see cref="HistoricalDataRequest"/>.</summary>
/// <param name="Rows">
/// Security, as Bloomberg echoed it, to its points in date order. A rejected security
/// is absent.
/// </param>
/// <param name="SecurityErrors">Securities Bloomberg rejected.</param>
/// <param name="FieldErrors">Fields Bloomberg rejected, per security.</param>
public sealed record HistoricalDataResponse(
    IReadOnlyDictionary<string, IReadOnlyList<HistoricalDataRow>> Rows,
    IReadOnlyList<SecurityError> SecurityErrors,
    IReadOnlyList<FieldError> FieldErrors);
