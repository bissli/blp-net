namespace Blp.HistoricalData;

/// <summary>Price a historical point carries.</summary>
public enum OverrideOption
{
    /// <summary>Bloomberg OVERRIDE_OPTION_CLOSE: the closing price.</summary>
    Close,

    /// <summary>Bloomberg OVERRIDE_OPTION_GPA: the average price.</summary>
    Gpa,
}
