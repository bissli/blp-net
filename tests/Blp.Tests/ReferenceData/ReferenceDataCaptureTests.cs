using System.Globalization;
using Bloomberglp.Blpapi;
using Bloomberglp.Blpapi.Test;
using Blp.ReferenceData;

namespace Blp.Tests.ReferenceData;

/// <summary>
/// <see cref="ReferenceDataHandler"/> over excerpts of responses a terminal sent, on
/// the real service shape.
/// </summary>
public class ReferenceDataCaptureTests
{
    // The capture host's wall clock: EST, which the zone rule of a time alone needs
    // fixed, because the parser dates it with the host's date.
    private static readonly TimeZoneInfo _est = TimeZoneInfo.CreateCustomTimeZone(
        "EST", TimeSpan.FromHours(-5), "EST", "EST");

    private static readonly string[] _induMembers = ["AAPL UW", "AMGN UW"];

    /// <summary>
    /// CreateRequest sends the securities, fields, override and UTC flag a terminal was
    /// sent, with the empty table overrides it adds.
    /// </summary>
    /// <remarks>
    /// Mutation: the override value or field id swapped, or useUTCTime left unset.
    /// Oracle: the printed request of the refdata_override capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedOverrideRequest()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(["IBM US Equity"], ["BEST_EPS"])
            {
                Overrides = new Dictionary<string, string> { ["BEST_FPERIOD_OVERRIDE"] = "1BF" },
            });

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            ReferenceDataRequest = {
                securities[] = {
                    IBM US Equity
                }
                fields[] = {
                    BEST_EPS
                }
                overrides[] = {
                    overrides = {
                        fieldId = "BEST_FPERIOD_OVERRIDE"
                        value = "1BF"
                    }
                }
                useUTCTime = true
                tableOverrides[] = {
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// Numbers, dates and strings keep their Bloomberg types: VOLUME is a float, a
    /// date is a DateOnly, and a time sent as a string stays a string.
    /// </summary>
    /// <remarks>
    /// Mutation: VOLUME read as an integer, a date left as text, or a STRING-typed time
    /// parsed into a TimeOnly.
    /// Oracle: the IBM values of the refdata_scalars capture, each with the datatype
    /// its message tree showed.
    /// </remarks>
    [Fact]
    public void ScalarsKeepTheirBloombergTypes()
    {
        var handler = new ReferenceDataHandler(new ReferenceDataRequest(
            ["IBM US Equity"],
            ["PX_LAST", "VOLUME", "NAME", "EQY_SH_OUT", "LAST_UPDATE_DT", "TIME"]));

        handler.ProcessResponse(IbmScalars());

        var values = Assert.Single(handler.ToResponse().Values).Value;
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["PX_LAST"] = 232.88,
                ["VOLUME"] = 2108146.0,
                ["NAME"] = "INTL BUSINESS MACHINES CORP",
                ["EQY_SH_OUT"] = 945.1207999999999,
                ["LAST_UPDATE_DT"] = new DateOnly(2026, 11, 17),
                ["TIME"] = "13:43:45",
            },
            values);
    }

    /// <summary>
    /// A time alone, which Bloomberg sends in UTC, is returned as a TimeOnly in the
    /// request's zone, whether the message types it a Datetime or a Time.
    /// </summary>
    /// <remarks>
    /// Mutation: the zone conversion dropped, which returns 18:43:45, or applied to one
    /// of the two datatypes only.
    /// Oracle: hand-worked: 18:43:45.199Z at -05:00 is 13:43:45.199. A fixed offset
    /// stands in for New York, because the parser dates a time alone with the host's
    /// date. Both values come from the refdata_scalars capture.
    /// </remarks>
    [Fact]
    public void TimeAloneIsATimeOnlyInTheRequestZone()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(["IBM US Equity"], ["LAST_UPDATE", "LAST_TRADE_TIME"])
            {
                TimeZone = _est,
            });

        handler.ProcessResponse(IbmScalars());

        var values = Assert.Single(handler.ToResponse().Values).Value;
        Assert.Equal(new TimeOnly(13, 43, 45), values["LAST_UPDATE"]);
        Assert.Equal(new TimeOnly(13, 43, 45, 199), values["LAST_TRADE_TIME"]);
    }

    /// <summary>
    /// With TimeAsDateTime a time alone is a DateTimeOffset dated at the host, in the
    /// request's zone.
    /// </summary>
    /// <remarks>
    /// Mutation: the offset left at UTC, the time of day shifted, or the date left at
    /// the minimum value.
    /// Oracle: hand-worked: 18:43:45.199Z at -05:00 is 13:43:45.199. The date is the
    /// host's, so it is held to within a day of today. The value comes from the
    /// refdata_scalars_time_as_datetime capture.
    /// </remarks>
    [Fact]
    public void TimeAsDateTimeGivesAnOffsetValueDatedToday()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(["IBM US Equity"], ["LAST_TRADE_TIME"])
            {
                TimeZone = _est,
                TimeAsDateTime = true,
            });

        handler.ProcessResponse(IbmScalars());

        var stamp = Assert.IsType<DateTimeOffset>(
            Assert.Single(handler.ToResponse().Values).Value["LAST_TRADE_TIME"]);
        Assert.Equal(TimeSpan.FromHours(-5), stamp.Offset);
        Assert.Equal(new TimeSpan(0, 13, 43, 45, 199), stamp.TimeOfDay);
        Assert.InRange(stamp.Date, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
    }

    /// <summary>
    /// ForceString gives every value as text: a float in plain notation with trailing
    /// zeros dropped, a date as its ISO form, and a time as Bloomberg prints it.
    /// </summary>
    /// <remarks>
    /// Mutation: a float written in its shortest round-trip form, which gives
    /// "945.1207999999999" and "220099733068.4", or the six decimals kept, which gives
    /// "2108146.000000".
    /// Oracle: the %f form to six decimals, worked out apart from the code:
    /// 945.1207999999999 is "945.120800" and 220099733068.4, the nearest double of
    /// which is 220099733068.399994, is "220099733068.399994". Values from the
    /// refdata_scalars_force_string capture.
    /// </remarks>
    [Fact]
    public void ForceStringGivesPlainNumberTextAndStampText()
    {
        var handler = new ReferenceDataHandler(new ReferenceDataRequest(
            ["IBM US Equity"],
            [
                "PX_LAST", "VOLUME", "EQY_SH_OUT", "CUR_MKT_CAP", "LAST_UPDATE_DT",
                "LAST_UPDATE", "LAST_TRADE_TIME",
            ])
        {
            ForceString = true,
        });

        handler.ProcessResponse(IbmScalars());

        var values = Assert.Single(handler.ToResponse().Values).Value;
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["PX_LAST"] = "232.88",
                ["VOLUME"] = "2108146",
                ["EQY_SH_OUT"] = "945.1208",
                ["CUR_MKT_CAP"] = "220099733068.399994",
                ["LAST_UPDATE_DT"] = "2026-11-17",
                ["LAST_UPDATE"] = "18:43:45.000",
                ["LAST_TRADE_TIME"] = "18:43:45.199",
            },
            values);
    }

    /// <summary>
    /// A bulk field gives one row per entry, keyed by the column names Bloomberg sent,
    /// and an empty bulk field gives no rows.
    /// </summary>
    /// <remarks>
    /// Mutation: rows flattened, column names with spaces or hyphens altered, a date
    /// column left as text, or an empty bulk field given as null.
    /// Oracle: the IBM dividend rows and the INDU members of the refdata_bulk capture.
    /// </remarks>
    [Fact]
    public void BulkFieldsGiveRowsKeyedByColumnName()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(
                ["IBM US Equity", "INDU Index"], ["DVD_HIST_ALL", "INDX_MEMBERS"]));

        handler.ProcessResponse(BulkResponse(dividendRows: 2));
        var response = handler.ToResponse();

        var ibm = response.Values["IBM US Equity"];
        Assert.Equal(
            [
                DividendRow("2026-10-21", "2026-11-10", "2026-11-10", "2026-12-10"),
                DividendRow("2026-07-22", "2026-08-10", "2026-08-10", "2026-09-10"),
            ],
            Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(ibm["DVD_HIST_ALL"]));
        Assert.Empty(
            Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(ibm["INDX_MEMBERS"]));
        var induMembers = Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(
            response.Values["INDU Index"]["INDX_MEMBERS"]);
        Assert.Equal(
            [
                new Dictionary<string, object?> { ["Member Ticker and Exchange Code"] = "AAPL UW" },
                new Dictionary<string, object?> { ["Member Ticker and Exchange Code"] = "AMGN UW" },
            ],
            induMembers);
        Assert.Equal(
            [
                new FieldError(
                    "IBM US Equity",
                    "INDX_MEMBERS",
                    "BAD_FLD",
                    "Field not applicable to security",
                    "NOT_APPLICABLE_TO_REF_DATA"),
                new FieldError(
                    "INDU Index",
                    "DVD_HIST_ALL",
                    "BAD_FLD",
                    "Field not applicable to security",
                    "NOT_APPLICABLE_TO_REF_DATA"),
            ],
            response.FieldErrors);
    }

    /// <summary>
    /// ForceString gives a bulk field as JSON text, one {column: value} object per
    /// cell, and an empty bulk field as "".
    /// </summary>
    /// <remarks>
    /// Mutation: a row's cells grouped into one object, numbers left unrounded, or an
    /// empty bulk field given as "[]".
    /// Oracle: hand-written from the first IBM dividend row of the
    /// refdata_bulk_force_string capture: one object per cell, ", " between objects,
    /// 1.69 as "1.69".
    /// </remarks>
    [Fact]
    public void ForceStringGivesBulkFieldAsJsonText()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(["IBM US Equity"], ["DVD_HIST_ALL", "INDX_MEMBERS"])
            {
                ForceString = true,
            });

        handler.ProcessResponse(BulkResponse(dividendRows: 1));

        var values = handler.ToResponse().Values["IBM US Equity"];
        Assert.Equal(
            "[{\"Declared Date\": \"2026-10-21\"}, {\"Ex-Date\": \"2026-11-10\"}, "
            + "{\"Record Date\": \"2026-11-10\"}, {\"Payable Date\": \"2026-12-10\"}, "
            + "{\"Dividend Amount\": \"1.69\"}, {\"Dividend Frequency\": \"Quarter\"}, "
            + "{\"Dividend Type\": \"Regular Cash\"}]",
            values["DVD_HIST_ALL"]);
        Assert.Equal("", values["INDX_MEMBERS"]);
    }

    /// <summary>
    /// A field Bloomberg rejects is null with its field error, and a security it
    /// rejects has no values and a security error.
    /// </summary>
    /// <remarks>
    /// Mutation: the rejected security kept in Values, the rejected field missing from
    /// the row, or the error's subcategory dropped.
    /// Oracle: the refdata_errors capture. Its source elements and the [nid:] suffix of
    /// the security error's message are left out of the excerpt.
    /// </remarks>
    [Fact]
    public void RejectedFieldIsNullAndRejectedSecurityIsAnError()
    {
        var handler = new ReferenceDataHandler(
            new ReferenceDataRequest(
                ["IBM US Equity", "NOTATICKER US Equity"], ["PX_LAST", "NOT_A_FIELD"]));

        handler.ProcessResponse(CapturedRefDataEvents.Response(
            "ReferenceDataRequest",
            Event.EventType.RESPONSE,
            """
            <ReferenceDataResponse>
              <securityData>
                <security>IBM US Equity</security>
                <fieldExceptions>
                  <fieldId>NOT_A_FIELD</fieldId>
                  <errorInfo>
                    <code>9</code>
                    <category>BAD_FLD</category>
                    <message>Field not valid</message>
                    <subcategory>INVALID_FIELD</subcategory>
                  </errorInfo>
                </fieldExceptions>
                <fieldData><PX_LAST>232.88</PX_LAST></fieldData>
                <sequenceNumber>0</sequenceNumber>
              </securityData>
              <securityData>
                <security>NOTATICKER US Equity</security>
                <fieldData/>
                <securityError>
                  <code>43</code>
                  <category>BAD_SEC</category>
                  <message>Unknown/Invalid Security</message>
                  <subcategory>INVALID_SECURITY</subcategory>
                </securityError>
                <sequenceNumber>1</sequenceNumber>
              </securityData>
            </ReferenceDataResponse>
            """));
        var response = handler.ToResponse();

        var values = Assert.Single(response.Values);
        Assert.Equal("IBM US Equity", values.Key);
        Assert.Equal(
            new Dictionary<string, object?> { ["PX_LAST"] = 232.88, ["NOT_A_FIELD"] = null },
            values.Value);
        Assert.Equal(
            new SecurityError(
                "NOTATICKER US Equity",
                "BAD_SEC",
                "Unknown/Invalid Security",
                "INVALID_SECURITY"),
            Assert.Single(response.SecurityErrors));
        Assert.Equal(
            new FieldError(
                "IBM US Equity", "NOT_A_FIELD", "BAD_FLD", "Field not valid", "INVALID_FIELD"),
            Assert.Single(response.FieldErrors));
    }

    private static Dictionary<string, object?> DividendRow(
        string declared, string exDate, string record, string payable) => new()
        {
            ["Declared Date"] = DateOnly.Parse(declared, CultureInfo.InvariantCulture),
            ["Ex-Date"] = DateOnly.Parse(exDate, CultureInfo.InvariantCulture),
            ["Record Date"] = DateOnly.Parse(record, CultureInfo.InvariantCulture),
            ["Payable Date"] = DateOnly.Parse(payable, CultureInfo.InvariantCulture),
            ["Dividend Amount"] = 1.69,
            ["Dividend Frequency"] = "Quarter",
            ["Dividend Type"] = "Regular Cash",
        };

    // The IBM securityData of refdata_scalars, trimmed to the fields the tests read.
    private static Event IbmScalars() => CapturedRefDataEvents.Response(
        "ReferenceDataRequest",
        Event.EventType.RESPONSE,
        response =>
        {
            response.PushElement(Name.GetName("securityData"));
            response.AppendElement();
            response.SetElement(Name.GetName("security"), "IBM US Equity");
            response.PushElement(Name.GetName("fieldData"));
            response.SetElement(Name.GetName("PX_LAST"), 232.88);
            response.SetElement(Name.GetName("VOLUME"), 2108146.0);
            response.SetElement(Name.GetName("NAME"), "INTL BUSINESS MACHINES CORP");
            response.SetElement(Name.GetName("EQY_SH_OUT"), 945.1207999999999);
            response.SetElement(Name.GetName("CUR_MKT_CAP"), 220099733068.4);
            response.SetElement(Name.GetName("LAST_UPDATE_DT"), new Datetime(2026, 11, 17));
            response.SetElement(Name.GetName("TIME"), "13:43:45");
            response.SetElement(
                Name.GetName("LAST_UPDATE"), new Datetime { Hour = 18, Minute = 43, Second = 45 });
            response.SetElement(
                Name.GetName("LAST_TRADE_TIME"),
                new Datetime { Hour = 18, Minute = 43, Second = 45, MilliSecond = 199 });
        });

    // IBM and INDU of refdata_bulk. A bulk column name holds a space or a hyphen, which
    // XML element names cannot, so the message is set through the formatter.
    private static Event BulkResponse(int dividendRows) => CapturedRefDataEvents.Response(
        "ReferenceDataRequest",
        Event.EventType.RESPONSE,
        response =>
        {
            response.PushElement(Name.GetName("securityData"));
            response.AppendElement();
            response.SetElement(Name.GetName("security"), "IBM US Equity");
            AppendFieldException(response, "INDX_MEMBERS");
            response.PushElement(Name.GetName("fieldData"));
            response.PushElement(Name.GetName("DVD_HIST_ALL"));
            (string Declared, string ExDate, string Record, string Payable)[] dividends =
            [
                ("2026-10-21", "2026-11-10", "2026-11-10", "2026-12-10"),
                ("2026-07-22", "2026-08-10", "2026-08-10", "2026-09-10"),
            ];
            foreach (var (declared, exDate, record, payable) in dividends.Take(dividendRows))
            {
                response.AppendElement();
                response.SetElement(Name.GetName("Declared Date"), Date(declared));
                response.SetElement(Name.GetName("Ex-Date"), Date(exDate));
                response.SetElement(Name.GetName("Record Date"), Date(record));
                response.SetElement(Name.GetName("Payable Date"), Date(payable));
                response.SetElement(Name.GetName("Dividend Amount"), 1.69);
                response.SetElement(Name.GetName("Dividend Frequency"), "Quarter");
                response.SetElement(Name.GetName("Dividend Type"), "Regular Cash");
                response.PopElement();
            }

            response.PopElement();
            response.PushElement(Name.GetName("INDX_MEMBERS"));
            response.PopElement();
            response.PopElement();
            response.PopElement();
            response.AppendElement();
            response.SetElement(Name.GetName("security"), "INDU Index");
            AppendFieldException(response, "DVD_HIST_ALL");
            response.PushElement(Name.GetName("fieldData"));
            response.PushElement(Name.GetName("DVD_HIST_ALL"));
            response.PopElement();
            response.PushElement(Name.GetName("INDX_MEMBERS"));
            foreach (string member in _induMembers)
            {
                response.AppendElement();
                response.SetElement(Name.GetName("Member Ticker and Exchange Code"), member);
                response.PopElement();
            }
        });

    private static void AppendFieldException(IMessageFormatter response, string fieldId)
    {
        response.PushElement(Name.GetName("fieldExceptions"));
        response.AppendElement();
        response.SetElement(Name.GetName("fieldId"), fieldId);
        response.PushElement(Name.GetName("errorInfo"));
        response.SetElement(Name.GetName("code"), 9);
        response.SetElement(Name.GetName("category"), "BAD_FLD");
        response.SetElement(Name.GetName("message"), "Field not applicable to security");
        response.SetElement(Name.GetName("subcategory"), "NOT_APPLICABLE_TO_REF_DATA");
        response.PopElement();
        response.PopElement();
        response.PopElement();
    }

    private static Datetime Date(string isoDate)
    {
        var date = DateOnly.Parse(isoDate, CultureInfo.InvariantCulture);
        return new Datetime(date.Year, date.Month, date.Day);
    }
}
