using System.Text.Json;
using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>Bloomberg element and message names.</summary>
internal static class ElementNames
{
    public static readonly Name AdjustmentAbnormal = Name.GetName("adjustmentAbnormal");
    public static readonly Name AdjustmentFollowDpdf = Name.GetName("adjustmentFollowDPDF");
    public static readonly Name AdjustmentNormal = Name.GetName("adjustmentNormal");
    public static readonly Name AdjustmentSplit = Name.GetName("adjustmentSplit");
    public static readonly Name BarData = Name.GetName("barData");
    public static readonly Name BarTickData = Name.GetName("barTickData");
    public static readonly Name CalendarCodeOverride = Name.GetName("calendarCodeOverride");
    public static readonly Name Category = Name.GetName("category");
    public static readonly Name Currency = Name.GetName("currency");
    public static readonly Name Date = Name.GetName("date");
    public static readonly Name EndDate = Name.GetName("endDate");
    public static readonly Name EndDateTime = Name.GetName("endDateTime");
    public static readonly Name ErrorInfo = Name.GetName("errorInfo");
    public static readonly Name EventType = Name.GetName("eventType");
    public static readonly Name EventTypes = Name.GetName("eventTypes");
    public static readonly Name FieldData = Name.GetName("fieldData");
    public static readonly Name FieldExceptions = Name.GetName("fieldExceptions");
    public static readonly Name FieldId = Name.GetName("fieldId");
    public static readonly Name Fields = Name.GetName("fields");
    public static readonly Name IncludeActionCodes = Name.GetName("includeActionCodes");
    public static readonly Name IncludeBicMicCodes = Name.GetName("includeBicMicCodes");
    public static readonly Name IncludeBrokerCodes = Name.GetName("includeBrokerCodes");
    public static readonly Name IncludeConditionCodes = Name.GetName("includeConditionCodes");
    public static readonly Name IncludeEqRefPrice = Name.GetName("includeEqRefPrice");
    public static readonly Name IncludeIndicatorCodes = Name.GetName("includeIndicatorCodes");
    public static readonly Name IncludeRpsCodes = Name.GetName("includeRpsCodes");
    public static readonly Name IncludeTradeDate = Name.GetName("includeTradeDate");
    public static readonly Name IncludeTradeTime = Name.GetName("includeTradeTime");
    public static readonly Name Interval = Name.GetName("interval");
    public static readonly Name MaxDataPoints = Name.GetName("maxDataPoints");
    public static readonly Name Message = Name.GetName("message");
    public static readonly Name NonTradingDayFillMethod = Name.GetName("nonTradingDayFillMethod");
    public static readonly Name NonTradingDayFillOption = Name.GetName("nonTradingDayFillOption");
    public static readonly Name OverrideOption = Name.GetName("overrideOption");
    public static readonly Name Overrides = Name.GetName("overrides");
    public static readonly Name PeriodicityAdjustment = Name.GetName("periodicityAdjustment");
    public static readonly Name PeriodicitySelection = Name.GetName("periodicitySelection");
    public static readonly Name PricingOption = Name.GetName("pricingOption");
    public static readonly Name Reason = Name.GetName("reason");
    public static readonly Name ResponseError = Name.GetName("responseError");
    public static readonly Name Securities = Name.GetName("securities");
    public static readonly Name Security = Name.GetName("security");
    public static readonly Name SecurityData = Name.GetName("securityData");
    public static readonly Name SecurityError = Name.GetName("securityError");
    public static readonly Name SessionTerminated = Name.GetName("SessionTerminated");
    public static readonly Name StartDate = Name.GetName("startDate");
    public static readonly Name StartDateTime = Name.GetName("startDateTime");
    public static readonly Name Subcategory = Name.GetName("subcategory");
    public static readonly Name TickData = Name.GetName("tickData");
    public static readonly Name UseUtcTime = Name.GetName("useUTCTime");
    public static readonly Name Value = Name.GetName("value");

    /// <summary>
    /// The Bloomberg enumeration value of an option, such as SEMI_ANNUALLY.
    /// </summary>
    /// <param name="option">An option enum value.</param>
    /// <returns>The value's name in upper snake case.</returns>
    public static string BloombergName(Enum option) =>
        JsonNamingPolicy.SnakeCaseUpper.ConvertName(option.ToString());
}
