namespace Blp.Eqs;

/// <summary>
/// A Bloomberg BeqsRequest (EQS): the securities a saved screen selects, with the
/// screen's columns.
/// </summary>
/// <param name="ScreenName">The screen's name, such as "Core Capital Ratios".</param>
public sealed record EqsRequest(string ScreenName)
{
    /// <summary>Where the screen is saved.</summary>
    public EqsScreenType ScreenType { get; init; } = EqsScreenType.Global;

    /// <summary>The screen's group. Null or empty sends none.</summary>
    public string? Group { get; init; } = "General";

    /// <summary>
    /// Runs the screen on the data as of this date, through the PiTDate override. Null
    /// runs it on current data.
    /// </summary>
    public DateOnly? AsOf { get; init; }

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
