using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.Internal;
using Microsoft.Extensions.Time.Testing;

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
    /// JsonStringLiteral escapes a quote, a backslash and every character outside
    /// printable ASCII.
    /// </summary>
    /// <remarks>
    /// Mutation: System.Text.Json escaping (a quote as a hex escape, upper-case hex,
    /// "/" or non-ASCII left raw), or a missed \b \f short escape.
    /// Oracle: hand-worked JSON escapes: lower-case hex, non-ASCII as UTF-16 units,
    /// short escapes for \b \t \n \f \r.
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
    public void JsonStringLiteralEscapesQuoteBackslashAndNonAscii(string text, string expected) =>
        Assert.Equal(expected, ElementParser.JsonStringLiteral(text));

    /// <summary>
    /// ForceString turns each element kind into text.
    /// </summary>
    /// <remarks>
    /// Mutation: a number left untrimmed, a string left unstripped, or a bulk field
    /// as one object per row instead of one per subelement.
    /// Oracle: hand-worked text for each element of the input: a trimmed number, a
    /// stripped string, a JSON list for the bulk field.
    /// </remarks>
    [Fact]
    public void ForceStringGivesText()
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
    /// ForceString writes a float as printf %f text, then trims it.
    /// </summary>
    /// <remarks>
    /// Mutation: the managed SDK's round-trip text used, or a tie rounded away from
    /// zero.
    /// Oracle: hand-worked printf %f text, six decimals with an exact tie rounded to
    /// even, then trailing zeros trimmed: 99.0078125 gives 99.007812.
    /// </remarks>
    [Theory]
    [InlineData("0.30000000000000004", "0.3")]
    [InlineData("1e-30", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("99.0078125", "99.007812")]
    [InlineData("123456789.1234567", "123456789.123457")]
    [InlineData("1e20", "100000000000000000000")]
    public void ForceStringFloatMatchesPrintfText(string sent, string expected)
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
    /// Oracle: any one time part makes a time; 14:30 UTC is 19:30 at +05:00, worked
    /// by hand.
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

    /// <summary>A time alone is returned as a time in the parser's zone.</summary>
    /// <remarks>
    /// Mutation: the zone conversion dropped, which returns 01:40:51.
    /// Oracle: hand-worked: 01:40:51Z at -04:00 is 21:40:51. A fixed offset stands in
    /// for New York, because the parser dates a time alone with the host's date.
    /// </remarks>
    [Fact]
    public void TimeAloneIsATimeInTheZone()
    {
        var minusFour = TimeZoneInfo.CreateCustomTimeZone(
            "Test-4", TimeSpan.FromHours(-4), "Test-4", "Test-4");
        var parser = new ElementParser(minusFour, forceString: false, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData("<LAST_TIME>01:40:51.000</LAST_TIME>");

        Assert.Equal(
            new TimeOnly(21, 40, 51), parser.SubelementValue(fieldData, Name.GetName("LAST_TIME")));
    }

    /// <summary>A time alone is dated with the clock's local date.</summary>
    /// <remarks>
    /// Mutation: the date taken from the UTC clock, which dates the stamp a day ahead.
    /// Oracle: hand-worked: a New York machine at 22:30 on June 16 sees June 16, and
    /// the value 23:59:58 UTC is 19:59:58 in New York.
    /// </remarks>
    [Fact]
    public void TimeAloneTakesTheClockLocalDate()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 17, 2, 30, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(newYork);
        var parser = new ElementParser(
            newYork, forceString: false, timeAsDateTime: true, timeProvider: clock);
        var fieldData = RefDataEvents.FieldData("<LAST_TIME>23:59:58.000</LAST_TIME>");

        Assert.Equal(
            new DateTimeOffset(2026, 6, 16, 19, 59, 58, TimeSpan.FromHours(-4)),
            parser.SubelementValue(fieldData, Name.GetName("LAST_TIME")));
    }

    /// <summary>
    /// A string element comes back as sent, even where it reads as a number.
    /// </summary>
    /// <remarks>
    /// Mutation: a string element parsed as a number under forceString, which drops the
    /// leading zero of the first CUSIP.
    /// Oracle: the input CUSIP itself.
    /// </remarks>
    [Theory]
    [InlineData("046433108", true)]
    [InlineData("046433108", false)]
    [InlineData("36831E108", true)]
    [InlineData("36831E108", false)]
    public void StringIdentifierIsKeptVerbatim(string cusip, bool forceString)
    {
        var parser = new ElementParser(_plusFive, forceString, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData($"<NAME>{cusip}</NAME>");

        Assert.Equal(cusip, parser.SubelementValue(fieldData, Name.GetName("NAME")));
    }

    /// <summary>
    /// A string element loses surrounding whitespace and keeps its digits.
    /// </summary>
    /// <remarks>
    /// Mutation: the string rounded as a number, which returns "1.5".
    /// Oracle: hand-worked: " 1.50 " trimmed is "1.50".
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StringValueIsStrippedAndNotRounded(bool forceString)
    {
        var parser = new ElementParser(_plusFive, forceString, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData("<NAME> 1.50 </NAME>");

        Assert.Equal("1.50", parser.SubelementValue(fieldData, Name.GetName("NAME")));
    }

    /// <summary>
    /// A number under forceString still has its trailing zeros dropped.
    /// </summary>
    /// <remarks>
    /// Mutation: rounding dropped for every type, so "7283.0" keeps its ".0".
    /// Oracle: hand-worked: "7283.0" with its trailing zeros dropped is "7283".
    /// </remarks>
    [Fact]
    public void NumberIsStillRoundedUnderForceString()
    {
        var parser = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData("<PX_LAST>7283.0</PX_LAST>");

        Assert.Equal("7283", parser.SubelementValue(fieldData, Name.GetName("PX_LAST")));
    }

    /// <summary>
    /// A bulk field's JSON keeps a string subelement and rounds a number.
    /// </summary>
    /// <remarks>
    /// Mutation: the string subelement parsed as a number, which turns the CUSIP into
    /// "46433108", or rounding dropped, which leaves "0.2500".
    /// Oracle: hand-worked JSON: the CUSIP string kept as sent, the weight 0.2500
    /// trimmed to 0.25.
    /// </remarks>
    [Fact]
    public void BulkFieldJsonRoundsOnlyNumericSubelements()
    {
        var parser = new ElementParser(_plusFive, forceString: true, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData(
            "<HOLDERS><name>046433108</name><weight>0.2500</weight></HOLDERS>");

        Assert.Equal(
            """[{"name": "046433108"}, {"weight": "0.25"}]""",
            parser.SubelementValue(fieldData, Name.GetName("HOLDERS")));
    }

    /// <summary>
    /// A bulk field whose rows omit different subelements has every name in every row.
    /// </summary>
    /// <remarks>
    /// Mutation: names taken from the first row only, which drops currency, first seen
    /// in the last row.
    /// Oracle: every row holds every name seen in any row, null where a row omits a
    /// field.
    /// </remarks>
    [Fact]
    public void BulkRowsWithDifferentSubelementsShareEveryName()
    {
        var parser = new ElementParser(_plusFive, forceString: false, timeAsDateTime: false);
        var fieldData = RefDataEvents.FieldData("""
            <HOLDERS><name>046433108</name><weight>0.25</weight></HOLDERS>
            <HOLDERS><name>009066101</name></HOLDERS>
            <HOLDERS>
              <name>00164V103</name><weight>0.30</weight><currency>USD</currency>
            </HOLDERS>
            """);

        var rows = Assert.IsType<List<IReadOnlyDictionary<string, object?>>>(
            parser.SubelementValue(fieldData, Name.GetName("HOLDERS")));
        Dictionary<string, object?> first = new()
        {
            ["name"] = "046433108",
            ["weight"] = 0.25,
            ["currency"] = null,
        };
        Dictionary<string, object?> second = new()
        {
            ["name"] = "009066101",
            ["weight"] = null,
            ["currency"] = null,
        };
        Dictionary<string, object?> third = new()
        {
            ["name"] = "00164V103",
            ["weight"] = 0.30,
            ["currency"] = "USD",
        };
        Assert.Equal(first, rows[0]);
        Assert.Equal(second, rows[1]);
        Assert.Equal(third, rows[2]);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(["name", "weight", "currency"], row.Keys));
    }

    /// <summary>
    /// A date-time on either side of a DST change takes the offset opendate gives.
    /// </summary>
    /// <remarks>
    /// Mutation: the offset read at the UTC wall clock as if it were zone time, or
    /// the zone's standard offset all year.
    /// Oracle: hand-worked: New York shifts at 07:00 UTC on March 10 and 06:00 UTC on
    /// November 3, London at 01:00 UTC on March 31, so 06:30 UTC on March 10 is 01:30
    /// at -05:00.
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
    /// A date-time keeps whole microseconds and drops finer ticks.
    /// </summary>
    /// <remarks>
    /// Mutation: the 100 ns tick kept, or the value rounded up.
    /// Oracle: hand-worked: .123456789 truncated to microseconds is .123456.
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
    /// Oracle: d06, which maps a missing field to null.
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
    /// Oracle: a null number, date or time is null and any other null is "".
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
    /// Oracle: every row holds one entry per name seen in any row.
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

    /// <summary>A sequence that is not an array gives no rows.</summary>
    /// <remarks>
    /// Mutation: the single sequence read as one row.
    /// Oracle: no rows for the typed value and "" for the text value.
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
