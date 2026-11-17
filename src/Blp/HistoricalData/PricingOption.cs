namespace Blp.HistoricalData;

/// <summary>Whether a fixed income point is a price or a yield.</summary>
public enum PricingOption
{
    /// <summary>Bloomberg PRICING_OPTION_PRICE.</summary>
    Price,

    /// <summary>Bloomberg PRICING_OPTION_YIELD.</summary>
    Yield,
}
