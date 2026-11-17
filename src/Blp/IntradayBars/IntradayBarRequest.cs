namespace Blp.IntradayBars;

/// <summary>
/// A Bloomberg IntradayBarRequest: open, high, low and close bars of one security.
/// </summary>
/// <param name="Ticker">Security identifier, such as "IBM US Equity".</param>
/// <param name="Start">First instant, inclusive. A DateTime converts at the host's
/// local offset unless its Kind is Utc.</param>
/// <param name="End">Last instant, inclusive. A DateTime converts as Start does.
/// </param>
public sealed record IntradayBarRequest(string Ticker, DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>
    /// Event type the bars summarize. Bloomberg rejects MidPrice and AtTrade.
    /// </summary>
    public IntradayEventType Event { get; init; } = IntradayEventType.Trade;

    /// <summary>Bar length in minutes, 1 to 1440.</summary>
    public int Interval { get; init; } = 1;

    /// <summary>Zone for date-time and time values in the response.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
}
