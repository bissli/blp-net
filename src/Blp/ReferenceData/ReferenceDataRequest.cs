using System.Collections.ObjectModel;

namespace Blp.ReferenceData;

/// <summary>
/// A Bloomberg ReferenceDataRequest (BDP): current field values per security.
/// </summary>
/// <param name="Tickers">Security identifiers, such as "IBM US Equity".</param>
/// <param name="Fields">Field mnemonics, such as "PX_LAST".</param>
public sealed record ReferenceDataRequest(
    IReadOnlyList<string> Tickers, IReadOnlyList<string> Fields)
{
    /// <summary>Field overrides, field id to value.</summary>
    public IReadOnlyDictionary<string, string> Overrides { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Zone for date-time and time values in the response.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>
    /// Return every value as a string: "" for null, a number in plain notation, a bulk
    /// field as JSON text in the Python blp's format.
    /// </summary>
    public bool ForceString { get; init; }

    /// <summary>
    /// Return a time-only value as a <see cref="DateTimeOffset"/> dated today.
    /// </summary>
    public bool TimeAsDateTime { get; init; }
}
