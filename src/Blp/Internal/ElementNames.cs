using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>Bloomberg element and message names.</summary>
internal static class ElementNames
{
    public static readonly Name Category = Name.GetName("category");
    public static readonly Name Currency = Name.GetName("currency");
    public static readonly Name Date = Name.GetName("date");
    public static readonly Name EndDate = Name.GetName("endDate");
    public static readonly Name ErrorInfo = Name.GetName("errorInfo");
    public static readonly Name FieldData = Name.GetName("fieldData");
    public static readonly Name FieldExceptions = Name.GetName("fieldExceptions");
    public static readonly Name FieldId = Name.GetName("fieldId");
    public static readonly Name Fields = Name.GetName("fields");
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
    public static readonly Name Subcategory = Name.GetName("subcategory");
    public static readonly Name UseUtcTime = Name.GetName("useUTCTime");
    public static readonly Name Value = Name.GetName("value");
}
