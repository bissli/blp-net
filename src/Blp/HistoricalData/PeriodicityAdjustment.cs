namespace Blp.HistoricalData;

/// <summary>Calendar a non-daily period follows.</summary>
public enum PeriodicityAdjustment
{
    /// <summary>Bloomberg ACTUAL.</summary>
    Actual,

    /// <summary>Bloomberg CALENDAR.</summary>
    Calendar,

    /// <summary>Bloomberg FISCAL.</summary>
    Fiscal,
}
