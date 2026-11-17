namespace Blp.HistoricalData;

/// <summary>Spacing of the points in a historical response.</summary>
public enum Periodicity
{
    /// <summary>Bloomberg DAILY.</summary>
    Daily,

    /// <summary>Bloomberg WEEKLY.</summary>
    Weekly,

    /// <summary>Bloomberg MONTHLY.</summary>
    Monthly,

    /// <summary>Bloomberg QUARTERLY.</summary>
    Quarterly,

    /// <summary>Bloomberg SEMI_ANNUALLY.</summary>
    SemiAnnually,

    /// <summary>Bloomberg YEARLY.</summary>
    Yearly,
}
