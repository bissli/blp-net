using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.Tests.Internal;

public class ElementParserTests
{
    private static readonly TimeZoneInfo _plusFive =
        TimeZoneInfo.CreateCustomTimeZone("Test+5", TimeSpan.FromHours(5), "Test+5", "Test+5");

    private static readonly Element _fieldData = RefDataEvents.FieldData("""
        <PX_LAST>7283.50</PX_LAST>
        <VOLUME>12</VOLUME>
        <NAME>  IBM  </NAME>
        <LAST_DATE>2024-03-15</LAST_DATE>
        <LAST_STAMP>2024-03-15T22:30:00.000+00:00</LAST_STAMP>
        <LAST_TIME>14:30:00.000</LAST_TIME>
        <HOLDERS><name>A</name><weight>1.5</weight></HOLDERS>
        <HOLDERS><name>B</name><weight>2.0</weight></HOLDERS>
        """);

    /// <summary>
    /// TrimNumber reproduces libb round_digit_string with places=None.
    /// </summary>
    /// <remarks>
    /// Mutation: exponent output ("G" format), no trim, a zero for an underflow, or
    /// a rounded value where round_digit_string keeps the text.
    /// Oracle: round_digit_string run on each input
    /// (.handoff/csharp-port/probes/trim_oracle.py).
    /// </remarks>
    [Theory]
    [InlineData("7283.0", "7283")]
    [InlineData("1E-05", "0.00001")]
    [InlineData("-0.0", "0")]
    [InlineData("  12.50 ", "12.5")]
    [InlineData("-1.25E+3", "-1250")]
    [InlineData("0001.200", "1.2")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("1.000000000000000000000000001", "1.000000000000000000000000001")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("1E-30", "1E-30")]
    [InlineData("1.5E-28", "1.5E-28")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
    [InlineData("1E+30", "1E+30")]
    [InlineData("inf", "inf")]
    [InlineData(" abc ", "abc")]
    public void TrimNumberMatchesRoundDigitString(string text, string expected) =>
        Assert.Equal(expected, ElementParser.TrimNumber(text));

    /// <summary>
    /// PythonJsonString escapes as Python's json.dumps does by default.
    /// </summary>
    /// <remarks>
    /// Mutation: System.Text.Json escaping (a quote as a hex escape, upper-case hex,
    /// "/" or non-ASCII left raw), or a missed \b \f short escape.
    /// Oracle: json.dumps run on each input
    /// (.handoff/csharp-port/probes/trim_oracle.py).
    /// </remarks>
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("quo\"te", "\"quo\\\"te\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("tab\tnl\ncr\rff\fbs\b", "\"tab\\tnl\\ncr\\rff\\fbs\\b\"")]
    [InlineData("nul\0", "\"nul\\u0000\"")]
    [InlineData("del\u007f", "\"del\\u007f\"")]
    [InlineData("caf\u00e9", "\"caf\\u00e9\"")]
    [InlineData("euro\u20ac", "\"euro\\u20ac\"")]
    [InlineData("emoji\U0001F600", "\"emoji\\ud83d\\ude00\"")]
    [InlineData("/slash ~tilde", "\"/slash ~tilde\"")]
    public void PythonJsonStringMatchesJsonDumps(string text, string expected) =>
        Assert.Equal(expected, ElementParser.PythonJsonString(text));

    /// <summary>
    /// ForceString turns each element kind into the Python blp's text.
    /// </summary>
    /// <remarks>
    /// Mutation: a number left untrimmed, a string left unstripped, or a bulk field
    /// as one object per row instead of one per subelement.
    /// Oracle: the Python blp's element_as_value with force_string=True, worked by
    /// hand on the same input.
    /// </remarks>
    [Fact]
    public void ForceStringGivesPythonText()
    {
        var parser = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);

        Assert.Equal("7283.5", Value(parser, "PX_LAST"));
        Assert.Equal("12", Value(parser, "VOLUME"));
        Assert.Equal("IBM", Value(parser, "NAME"));
        Assert.Equal(
            """[{"name": "A"}, {"weight": "1.5"}, {"name": "B"}, {"weight": "2"}]""",
            Value(parser, "HOLDERS"));
    }

    /// <summary>
    /// ForceString writes a float as the Python SDK's printf %f text, then trims it.
    /// </summary>
    /// <remarks>
    /// Mutation: the managed SDK's round-trip text used, or a tie rounded away from
    /// zero.
    /// Oracle: the Python blpapi 3.26.10.1 getValueAsString text through
    /// round_digit_string (.handoff/csharp-port/probes/sdk_text.py).
    /// </remarks>
    [Theory]
    [InlineData("0.30000000000000004", "0.3")]
    [InlineData("1e-30", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("99.0078125", "99.007812")]
    [InlineData("123456789.1234567", "123456789.123457")]
    [InlineData("1e20", "100000000000000000000")]
    public void ForceStringFloatMatchesPythonSdkText(string sent, string expected)
    {
        var parser = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData($"<PX_LAST>{sent}</PX_LAST>");

        Assert.Equal(expected, parser.SubelementValue(fieldData, Name.GetName("PX_LAST")));
    }

    /// <summary>Without ForceString, each element kind keeps its typed value.</summary>
    /// <remarks>
    /// Mutation: a number returned as text, or a string left unstripped.
    /// Oracle: the input values.
    /// </remarks>
    [Fact]
    public void TypedValuesKeepTheirType()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);

        Assert.Equal(7283.5, Value(parser, "PX_LAST"));
        Assert.Equal(12L, Value(parser, "VOLUME"));
        Assert.Equal("IBM", Value(parser, "NAME"));
        Assert.Equal(new DateOnly(2024, 3, 15), Value(parser, "LAST_DATE"));
    }

    /// <summary>A date-time is read as UTC and returned in the parser's zone.</summary>
    /// <remarks>
    /// Mutation: the wall clock returned unconverted, or converted as if local time.
    /// Oracle: 22:30 UTC on 15 March is 03:30 on 16 March at +05:00, worked by hand.
    /// </remarks>
    [Fact]
    public void DateTimeConvertsFromUtcToTheZone()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);

        var stamp = Assert.IsType<DateTimeOffset>(Value(parser, "LAST_STAMP"));
        Assert.Equal(new DateTime(2024, 3, 16, 3, 30, 0), stamp.DateTime);
        Assert.Equal(TimeSpan.FromHours(5), stamp.Offset);
    }

    /// <summary>
    /// A time alone converts to the zone, as a time or a dated stamp.
    /// </summary>
    /// <remarks>
    /// Mutation: timeAsDateTime ignored, or the time left in UTC.
    /// Oracle: 14:30 UTC is 19:30 at +05:00, worked by hand.
    /// </remarks>
    [Fact]
    public void TimeConvertsFromUtcToTheZone()
    {
        var asTime = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var asStamp = new ElementParser(_plusFive, forceString: false, timeAsDateTime: true);

        Assert.Equal(new TimeOnly(19, 30), Value(asTime, "LAST_TIME"));
        var stamp = Assert.IsType<DateTimeOffset>(Value(asStamp, "LAST_TIME"));
        Assert.Equal(new TimeSpan(19, 30, 0), stamp.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(5), stamp.Offset);
    }

    /// <summary>A time with only hours and minutes set is still a time.</summary>
    /// <remarks>
    /// Mutation: !value.HasParts(Datetime.TIME) in place of the TimeParts test, which
    /// reads a time without seconds as a date.
    /// Oracle: Python blpapi, which counts any one time part as a time; 14:30 UTC is
    /// 19:30 at +05:00, worked by hand.
    /// </remarks>
    [Fact]
    public void TimeWithoutSecondsGivesTime()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var hoursAndMinutes = new Datetime { Hour = 14, Minute = 30 };
        var fieldData = RefDataEvents.FieldData(
            formatter => formatter.SetElement(Name.GetName("LAST_TIME"), hoursAndMinutes));

        Assert.Equal(
            new TimeOnly(19, 30), parser.SubelementValue(fieldData, Name.GetName("LAST_TIME")));
    }

    /// <summary>
    /// A date-time on either side of a DST change takes the offset opendate gives.
    /// </summary>
    /// <remarks>
    /// Mutation: the offset read at the UTC wall clock as if it were zone time, or
    /// the zone's standard offset all year.
    /// Oracle: the Python blp Parser on opendate
    /// (.handoff/csharp-port/probes/opendate_diff_python.txt).
    /// </remarks>
    [Theory]
    [InlineData("America/New_York", "2024-03-10T06:30:00", "2024-03-10T01:30:00-05:00")]
    [InlineData("America/New_York", "2024-03-10T07:00:00", "2024-03-10T03:00:00-04:00")]
    [InlineData("America/New_York", "2024-11-03T05:30:00", "2024-11-03T01:30:00-04:00")]
    [InlineData("America/New_York", "2024-11-03T06:00:00", "2024-11-03T01:00:00-05:00")]
    [InlineData("America/New_York", "2024-11-03T06:30:00", "2024-11-03T01:30:00-05:00")]
    [InlineData("Europe/London", "2024-03-31T00:30:00", "2024-03-31T00:30:00+00:00")]
    [InlineData("Europe/London", "2024-03-31T01:30:00", "2024-03-31T02:30:00+01:00")]
    [InlineData("Europe/London", "2024-10-27T00:30:00", "2024-10-27T01:30:00+01:00")]
    [InlineData("Europe/London", "2024-10-27T01:30:00", "2024-10-27T01:30:00+00:00")]
    [InlineData("Australia/Sydney", "2024-04-06T15:30:00", "2024-04-07T02:30:00+11:00")]
    [InlineData("Australia/Sydney", "2024-04-06T16:30:00", "2024-04-07T02:30:00+10:00")]
    [InlineData("Australia/Sydney", "2024-10-05T15:30:00", "2024-10-06T01:30:00+10:00")]
    [InlineData("Australia/Sydney", "2024-10-05T16:30:00", "2024-10-06T03:30:00+11:00")]
    public void DateTimeAtDstChangeMatchesOpendate(string zoneId, string sentUtc, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var parser = new ElementParser(zone, forceString: false, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData($"<LAST_STAMP>{sentUtc}.000+00:00</LAST_STAMP>");

        var stamp = Assert.IsType<DateTimeOffset>(
            parser.SubelementValue(fieldData, Name.GetName("LAST_STAMP")));
        Assert.Equal(
            expected, stamp.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A date-time keeps whole microseconds, as a Python datetime does.
    /// </summary>
    /// <remarks>
    /// Mutation: the 100 ns tick kept, or the value rounded up.
    /// Oracle: Python blpapi, which truncates to microseconds.
    /// </remarks>
    [Fact]
    public void DateTimeTruncatesToMicroseconds()
    {
        var parser = new ElementParser(TimeZoneInfo.Utc, forceString: false, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData(
            "<LAST_STAMP>2024-03-15T14:30:00.123456789+00:00</LAST_STAMP>");

        var stamp = Assert.IsType<DateTimeOffset>(
            parser.SubelementValue(fieldData, Name.GetName("LAST_STAMP")));
        Assert.Equal(new TimeSpan(14, 30, 0) + TimeSpan.FromTicks(1_234_560), stamp.TimeOfDay);
    }

    /// <summary>A bulk field becomes one name-to-value row per entry.</summary>
    /// <remarks>
    /// Mutation: rows flattened into one, or subelement values left as text.
    /// Oracle: the input rows.
    /// </remarks>
    [Fact]
    public void BulkFieldGivesTypedRows()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);

        var rows = Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(
            Value(parser, "HOLDERS"));
        Assert.Equal(2, rows.Count);
        Dictionary<string, object?> first = new() { ["name"] = "A", ["weight"] = 1.5 };
        Dictionary<string, object?> second = new() { ["name"] = "B", ["weight"] = 2.0 };
        Assert.Equal(first, rows[0]);
        Assert.Equal(second, rows[1]);
    }

    /// <summary>A field the element lacks gives null.</summary>
    /// <remarks>
    /// Mutation: "" or NaN for a missing field, or an exception from GetElement.
    /// Oracle: d06, which maps the Python blp's NaN for a missing field to null.
    /// </remarks>
    [Fact]
    public void MissingFieldGivesNull()
    {
        var parser = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);
        var sparse = RefDataEvents.FieldData("<NAME>IBM</NAME>");

        Assert.Null(parser.SubelementValue(sparse, Name.GetName("PX_LAST")));
    }

    /// <summary>
    /// A null number, date or time gives null, and any other null gives "".
    /// </summary>
    /// <remarks>
    /// Mutation: null for every null, or "" for a null number.
    /// Oracle: the Python Parser, which gives NaN, NaT and '' for these nulls.
    /// </remarks>
    [Fact]
    public void NullValueGivesNullOrEmpty()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var nulls = RefDataEvents.FieldData(formatter =>
        {
            formatter.SetElementNull(Name.GetName("NAME"));
            formatter.SetElementNull(Name.GetName("PX_LAST"));
            formatter.SetElementNull(Name.GetName("LAST_DATE"));
        });

        Assert.Equal("", parser.SubelementValue(nulls, Name.GetName("NAME")));
        Assert.Null(parser.SubelementValue(nulls, Name.GetName("PX_LAST")));
        Assert.Null(parser.SubelementValue(nulls, Name.GetName("LAST_DATE")));
    }

    /// <summary>A date is the calendar date sent, whatever the parser's zone.</summary>
    /// <remarks>
    /// Mutation: the date read as UTC midnight and converted to the zone.
    /// Oracle: the sent date; New York midnight UTC falls on the day before.
    /// </remarks>
    [Fact]
    public void DateIgnoresTheZone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var parser = new ElementParser(newYork, forceString: false, timeAsDateTime: false);

        Assert.Equal(new DateOnly(2024, 3, 15), Value(parser, "LAST_DATE"));
    }

    /// <summary>A bulk row that omits a subelement holds null under its name.</summary>
    /// <remarks>
    /// Mutation: each row keyed only by its own subelements.
    /// Oracle: the Python Parser, which gives one column per name seen in any row.
    /// </remarks>
    [Fact]
    public void BulkRowMissingSubelementGivesNull()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData("""
            <HOLDERS><name>A</name><weight>1.5</weight></HOLDERS>
            <HOLDERS><name>B</name></HOLDERS>
            """);

        var rows = Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(
            parser.SubelementValue(fieldData, Name.GetName("HOLDERS")));
        Dictionary<string, object?> second = new() { ["name"] = "B", ["weight"] = null };
        Assert.Equal(second, rows[1]);
    }

    /// <summary>A sequence that is not an array gives no rows, as in Python.</summary>
    /// <remarks>
    /// Mutation: the single sequence read as one row.
    /// Oracle: the Python Parser, which gives an empty frame and ''.
    /// </remarks>
    [Fact]
    public void SingleSequenceGivesNoRows()
    {
        var typed = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var asText = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData(
            "<TOP_HOLDER><name>A</name><weight>1.5</weight></TOP_HOLDER>");
        var topHolder = Name.GetName("TOP_HOLDER");

        Assert.Empty(Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(
            typed.SubelementValue(fieldData, topHolder)));
        Assert.Equal("", asText.SubelementValue(fieldData, topHolder));
    }

    private static object? Value(ElementParser parser, string field) =>
        parser.SubelementValue(_fieldData, Name.GetName(field));
}
