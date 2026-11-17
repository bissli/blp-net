using System.Collections.ObjectModel;

namespace Blp.HistoricalData;

/// <summary>A Bloomberg HistoricalDataRequest (BDH): field values per date.</summary>
/// <param name="Tickers">Security identifiers, such as "IBM US Equity".</param>
/// <param name="Fields">Field mnemonics, such as "PX_LAST".</param>
/// <param name="Start">First date, inclusive. Null is the day before End.</param>
/// <param name="End">Last date, inclusive. Null is this host's local date.</param>
public sealed record HistoricalDataRequest(
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Fields,
    DateOnly? Start = null,
    DateOnly? End = null)
{
    /// <summary>Spacing of the points.</summary>
    public Periodicity Period { get; init; } = Periodicity.Daily;

    /// <summary>
    /// Calendar a non-daily period follows. Null leaves Bloomberg's default.
    /// </summary>
    public PeriodicityAdjustment? PeriodAdjustment { get; init; }

    /// <summary>
    /// ISO code to convert values into. Null keeps the security's currency.
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// Price each point carries. Null leaves Bloomberg's default.
    /// </summary>
    public OverrideOption? OverrideOption { get; init; }

    /// <summary>
    /// Price or yield for fixed income. Null leaves Bloomberg's default.
    /// </summary>
    public PricingOption? PricingOption { get; init; }

    /// <summary>
    /// Which non-trading days to include. Null leaves Bloomberg's default.
    /// </summary>
    public NonTradingDayFillOption? NonTradingDayFillOption { get; init; }

    /// <summary>
    /// Value a non-trading day carries. Null leaves Bloomberg's default.
    /// </summary>
    public NonTradingDayFillMethod? NonTradingDayFillMethod { get; init; }

    /// <summary>
    /// Cap on points per security, counted back from End. Null sets none.
    /// </summary>
    public int? MaxDataPoints { get; init; }

    /// <summary>
    /// Apply normal pricing adjustments. Null leaves Bloomberg's default.
    /// </summary>
    public bool? AdjustmentNormal { get; init; }

    /// <summary>
    /// Apply abnormal pricing adjustments. Null leaves Bloomberg's default.
    /// </summary>
    public bool? AdjustmentAbnormal { get; init; }

    /// <summary>Apply split adjustments. Null leaves Bloomberg's default.</summary>
    public bool? AdjustmentSplit { get; init; }

    /// <summary>
    /// Follow the terminal's DPDF adjustment settings. Null leaves Bloomberg's default.
    /// </summary>
    public bool? AdjustmentFollowDpdf { get; init; }

    /// <summary>
    /// Calendar code that replaces the security's own. Null leaves Bloomberg's default.
    /// </summary>
    public string? CalendarCodeOverride { get; init; }

    /// <summary>Field overrides, field id to value.</summary>
    public IReadOnlyDictionary<string, string> Overrides { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Zone for date-time and time values in the response.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>
    /// Throw <see cref="RequestSecurityException"/> when the response holds a security
    /// error. Checked before <see cref="RaiseFieldError"/>.
    /// </summary>
    public bool RaiseSecurityError { get; init; }

    /// <summary>
    /// Throw <see cref="RequestFieldException"/> when the response holds a field error.
    /// </summary>
    public bool RaiseFieldError { get; init; }

    /// <summary>
    /// Longest wait for the next event. Each partial response restarts it. Null takes
    /// <see cref="BlpClientOptions.IdleTimeout"/>.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// Longest wait for the whole response, which no partial response restarts. Null
    /// takes <see cref="BlpClientOptions.TotalTimeout"/>.
    /// </summary>
    public TimeSpan? TotalTimeout { get; init; }
}
