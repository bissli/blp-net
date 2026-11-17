namespace Blp.IntradayTicks;

/// <summary>
/// A Bloomberg IntradayTickRequest: every tick of one security in a time range.
/// </summary>
/// <param name="Ticker">Security identifier, such as "IBM US Equity".</param>
/// <param name="Start">First instant, inclusive. A DateTime converts at the host's
/// local offset unless its Kind is Utc.</param>
/// <param name="End">Last instant, inclusive. A DateTime converts as Start does.
/// </param>
public sealed record IntradayTickRequest(string Ticker, DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Event types to return.</summary>
    public IReadOnlyList<IntradayEventType> Events { get; init; } =
        [IntradayEventType.Trade, IntradayEventType.Ask, IntradayEventType.Bid];

    /// <summary>Add each tick's action codes.</summary>
    public bool IncludeActionCodes { get; init; }

    /// <summary>Add each tick's BIC and MIC codes.</summary>
    public bool IncludeBicMicCodes { get; init; }

    /// <summary>Add each tick's broker codes.</summary>
    public bool IncludeBrokerCodes { get; init; }

    /// <summary>Add each tick's condition codes.</summary>
    public bool IncludeConditionCodes { get; init; }

    /// <summary>Add each tick's equity reference price.</summary>
    public bool IncludeEquityRefPrice { get; init; }

    /// <summary>Add each tick's indicator codes.</summary>
    public bool IncludeIndicatorCodes { get; init; }

    /// <summary>Add each tick's reporting party side codes.</summary>
    public bool IncludeRpsCodes { get; init; }

    /// <summary>Add each tick's trade date.</summary>
    public bool IncludeTradeDate { get; init; }

    /// <summary>Add each tick's trade time.</summary>
    public bool IncludeTradeTime { get; init; }

    /// <summary>Zone for date-time and time values in the response.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

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
