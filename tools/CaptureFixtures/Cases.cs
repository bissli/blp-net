using Bloomberglp.Blpapi;

namespace CaptureFixtures;

/// <summary>One Bloomberg request to send and record.</summary>
/// <param name="Name">File stem of the case record.</param>
/// <param name="Service">URI of the service that takes the request.</param>
/// <param name="Operation">Request operation name in that service.</param>
/// <param name="Fill">Sets the elements of a request created for the operation.</param>
internal sealed record CaptureCase(
    string Name, string Service, string Operation, Action<Request> Fill);

/// <summary>The request cases and the subscription topics and fields.</summary>
internal static class Cases
{
    /// <summary>Topics of both subscription captures.</summary>
    public static readonly string[] SubscribeTopics =
        ["IBM US Equity", "SPX Index", "NOTATICKER US Equity"];

    /// <summary>Fields of both subscription captures.</summary>
    public static readonly string[] SubscribeFields =
        ["LAST_PRICE", "BID", "ASK", "VOLUME", "TIME", "LAST_TRADE"];

    private const string RefDataService = "//blp/refdata";
    private const string BqlService = "//blp/bqlsvc";
    private const string ResponseZone = "America/New_York";

    private static readonly string[] _scalarTickers =
        ["IBM US Equity", "AAPL US Equity", "SPX Index"];

    private static readonly string[] _scalarFields =
    [
        "PX_LAST", "VOLUME", "NAME", "CRNCY", "ID_BB_GLOBAL", "EQY_SH_OUT", "CUR_MKT_CAP",
        "LAST_UPDATE_DT", "TIME", "LAST_UPDATE", "DVD_EX_DT", "PX_YEST_DT", "TRADING_DT_REALTIME",
        "LAST_TRADE_TIME", "LAST_UPDATE_DATE_EOD", "BID", "ASK", "PX_OPEN",
    ];

    private static readonly string[] _bulkTickers = ["IBM US Equity", "INDU Index"];
    private static readonly string[] _bulkFields = ["DVD_HIST_ALL", "INDX_MEMBERS"];
    private static readonly string[] _errorTickers = ["IBM US Equity", "NOTATICKER US Equity"];
    private static readonly string[] _errorFields = ["PX_LAST", "NOT_A_FIELD"];

    private static readonly string[] _manyTickers =
    [
        "IBM US Equity", "AAPL US Equity", "MSFT US Equity", "JPM US Equity", "XOM US Equity",
        "KO US Equity", "PG US Equity", "SPX Index",
    ];

    private static readonly string[] _tickFlags =
    [
        "includeConditionCodes", "includeBrokerCodes", "includeEqRefPrice", "includeRpsCodes",
        "includeBicMicCodes", "includeActionCodes", "includeIndicatorCodes", "includeTradeDate",
        "includeTradeTime",
    ];

    /// <summary>Every request case, in the order they are sent.</summary>
    /// <param name="day">Trading day the intraday cases read.</param>
    /// <param name="eqsScreen">Global or saved EQS screen name.</param>
    /// <returns>
    /// The cases. Each intraday window opens at 09:30 America/New_York on
    /// <paramref name="day"/>, the US equity open.
    /// </returns>
    public static IReadOnlyList<CaptureCase> Build(DateOnly day, string eqsScreen)
    {
        var sessionOpen = TimeZoneInfo.ConvertTimeToUtc(
            day.ToDateTime(new TimeOnly(9, 30)), TimeZoneInfo.FindSystemTimeZoneById(ResponseZone));
        return
        [
            ReferenceData("refdata_scalars", _scalarTickers, _scalarFields),
            ReferenceData("refdata_scalars_host_zone", _scalarTickers, _scalarFields),
            ReferenceData("refdata_scalars_force_string", _scalarTickers, _scalarFields),
            ReferenceData("refdata_scalars_time_as_datetime", _scalarTickers, _scalarFields),
            ReferenceData("refdata_bulk", _bulkTickers, _bulkFields),
            ReferenceData("refdata_bulk_force_string", _bulkTickers, _bulkFields),
            ReferenceData("refdata_errors", _errorTickers, _errorFields),
            ReferenceData(
                "refdata_override", ["IBM US Equity"], ["BEST_EPS"],
                ("BEST_FPERIOD_OVERRIDE", "1BF")),
            History(
                "hist_daily", ["IBM US Equity", "SPX Index"], ["PX_LAST", "VOLUME"],
                "20240102", "20240329"),
            History(
                "hist_weekly_errors", _errorTickers, _errorFields, "20240102", "20240329",
                period: "WEEKLY"),
            History(
                "hist_options", ["IBM US Equity"], ["PX_LAST"], "20240301", "20240331",
                extra: request =>
                {
                    request.Set(Key("currency"), "EUR");
                    request.Set(Key("nonTradingDayFillOption"), "ALL_CALENDAR_DAYS");
                    request.Set(Key("nonTradingDayFillMethod"), "NIL_VALUE");
                    request.Set(Key("maxDataPoints"), 5);
                }),
            History(
                "hist_pricing_options", ["IBM US Equity"], ["PX_LAST", "BEST_EPS"],
                "20230101", "20241231", period: "QUARTERLY",
                extra: request =>
                {
                    request.Set(Key("periodicityAdjustment"), "CALENDAR");
                    request.Set(Key("overrideOption"), "OVERRIDE_OPTION_GPA");
                    request.Set(Key("pricingOption"), "PRICING_OPTION_PRICE");
                    AppendOverride(request, "BEST_FPERIOD_OVERRIDE", "1BF");
                }),
            History("hist_partial", _manyTickers, ["PX_LAST"], "20150102", "20241231"),
            IntradayTicks(
                "intraday_ticks", ["BID", "ASK", "TRADE"], sessionOpen,
                TimeSpan.FromMinutes(10), withFlags: true),
            IntradayTicks(
                "intraday_ticks_plain", ["TRADE", "ASK", "BID"], sessionOpen,
                TimeSpan.FromMinutes(2), withFlags: false),
            IntradayBars("intraday_bars", sessionOpen, TimeSpan.FromMinutes(390), 5),
            IntradayBars(
                "intraday_bars_default_interval", sessionOpen, TimeSpan.FromMinutes(30), 1),
            Bql("bql", "get(px_last) for(['IBM US Equity', 'AAPL US Equity'])"),
            Bql(
                "bql_dates",
                "get(px_last(dates=range(-5d, 0d)), name) "
                + "for(['IBM US Equity', 'AAPL US Equity'])"),
            Bql(
                "bql_errors",
                "get(not_a_field) for(['IBM US Equity', 'NOTATICKER US Equity'])"),
            Eqs("eqs", eqsScreen),
            Eqs("eqs_unknown_screen", "NOT A SCREEN"),
        ];
    }

    private static Name Key(string element) => Name.GetName(element);

    private static void AppendAll(Request request, string element, IEnumerable<string> values)
    {
        foreach (string value in values) request.Append(Key(element), value);
    }

    private static void AppendOverride(Request request, string fieldId, string value)
    {
        var entry = request.GetElement(Key("overrides")).AppendElement();
        entry.SetElement(Key("fieldId"), fieldId);
        entry.SetElement(Key("value"), value);
    }

    private static CaptureCase ReferenceData(
        string name, string[] tickers, string[] fields,
        params (string FieldId, string Value)[] overrides) =>
        new(name, RefDataService, "ReferenceDataRequest", request =>
        {
            AppendAll(request, "securities", tickers);
            AppendAll(request, "fields", fields);
            request.Set(Key("useUTCTime"), true);
            foreach (var (fieldId, value) in overrides) AppendOverride(request, fieldId, value);
        });

    private static CaptureCase History(
        string name, string[] tickers, string[] fields, string start, string end,
        string period = "DAILY", Action<Request>? extra = null) =>
        new(name, RefDataService, "HistoricalDataRequest", request =>
        {
            AppendAll(request, "securities", tickers);
            AppendAll(request, "fields", fields);
            request.Set(Key("startDate"), start);
            request.Set(Key("endDate"), end);
            request.Set(Key("periodicitySelection"), period);
            extra?.Invoke(request);
        });

    private static CaptureCase IntradayTicks(
        string name, string[] events, DateTime start, TimeSpan length, bool withFlags) =>
        new(name, RefDataService, "IntradayTickRequest", request =>
        {
            request.Set(Key("security"), "IBM US Equity");
            AppendAll(request, "eventTypes", events);
            request.Set(Key("startDateTime"), new Datetime(start));
            request.Set(Key("endDateTime"), new Datetime(start + length));
            if (!withFlags) return;
            foreach (string flag in _tickFlags) request.Set(Key(flag), true);
        });

    private static CaptureCase IntradayBars(
        string name, DateTime start, TimeSpan length, int interval) =>
        new(name, RefDataService, "IntradayBarRequest", request =>
        {
            request.Set(Key("security"), "IBM US Equity");
            request.Set(Key("eventType"), "TRADE");
            request.Set(Key("startDateTime"), new Datetime(start));
            request.Set(Key("endDateTime"), new Datetime(start + length));
            request.Set(Key("interval"), interval);
        });

    private static CaptureCase Bql(string name, string expression) =>
        new(name, BqlService, "sendQuery", request =>
        {
            request.Set(Key("expression"), expression);
            request.GetElement(Key("clientContext")).SetElement(Key("appName"), "EXCEL");
        });

    private static CaptureCase Eqs(string name, string screen) =>
        new(name, RefDataService, "BeqsRequest", request =>
        {
            request.Set(Key("screenName"), screen);
            request.Set(Key("screenType"), "GLOBAL");
            request.Set(Key("Group"), "General");
        });
}
