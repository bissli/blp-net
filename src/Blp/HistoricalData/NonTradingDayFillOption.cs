namespace Blp.HistoricalData;

/// <summary>Which non-trading days a historical response includes.</summary>
public enum NonTradingDayFillOption
{
    /// <summary>Bloomberg NON_TRADING_WEEKDAYS.</summary>
    NonTradingWeekdays,

    /// <summary>Bloomberg ALL_CALENDAR_DAYS.</summary>
    AllCalendarDays,

    /// <summary>Bloomberg ACTIVE_DAYS_ONLY.</summary>
    ActiveDaysOnly,
}
