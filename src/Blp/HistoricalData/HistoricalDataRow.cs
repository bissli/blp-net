namespace Blp.HistoricalData;

/// <summary>One point of a historical response.</summary>
/// <param name="Date">The point's date.</param>
/// <param name="Values">
/// Field to value. Every requested field is present, null where Bloomberg sent none.
/// </param>
public sealed record HistoricalDataRow(DateOnly Date, IReadOnlyDictionary<string, object?> Values);
