using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>Converts Bloomberg response elements to .NET values.</summary>
/// <param name="timeZone">
/// Zone for returned date-times and times. Bloomberg sends UTC.
/// </param>
/// <param name="forceString">
/// Return every value as a string (see <see cref="ToValue"/>).
/// </param>
/// <param name="timeAsDateTime">
/// Return a time-only value as a <see cref="DateTimeOffset"/> dated today.
/// </param>
internal sealed partial class ElementParser(
    TimeZoneInfo timeZone, bool forceString, bool timeAsDateTime)
{
    // libb round_digit_string keeps the text of a number past this many digits.
    private const int MaxPlainDigits = 28;

    // Python blpapi counts a value as a time when any one of these parts is set.
    // Datetime.TIME needs hours, minutes and seconds all set.
    private const int TimeParts = Datetime.HOUR_PART | Datetime.MINUTE_PART
        | Datetime.SECOND_PART | Datetime.MILLISECOND_PART | Datetime.MICROSECOND_PART
        | Datetime.NANOSECOND_PART | Datetime.PICOSECOND_PART;

    private readonly TimeZoneInfo _timeZone = timeZone;
    private readonly bool _forceString = forceString;
    private readonly bool _timeAsDateTime = timeAsDateTime;

    /// <summary>The messages of responseEvent, in order.</summary>
    /// <param name="responseEvent">A PARTIAL_RESPONSE or RESPONSE event.</param>
    /// <returns>The messages, read lazily.</returns>
    /// <exception cref="ResponseException">A message holds a responseError.</exception>
    public static IEnumerable<Message> CheckedMessages(Event responseEvent)
    {
        foreach (var message in responseEvent)
        {
            if (message.HasElement(ElementNames.ResponseError))
            {
                var error = message.GetElement(ElementNames.ResponseError);
                throw new ResponseException(
                    $"REQUEST FAILED: {error}",
                    OptionalString(error, ElementNames.Category),
                    OptionalString(error, ElementNames.Subcategory));
            }

            yield return message;
        }
    }

    /// <summary>The securityError of one securityData entry, or null.</summary>
    /// <param name="securityData">One securityData entry.</param>
    /// <returns>The error, or null when the entry has none.</returns>
    public static SecurityError? SecurityErrorOf(Element securityData)
    {
        if (!securityData.HasElement(ElementNames.SecurityError)) return null;
        var error = securityData.GetElement(ElementNames.SecurityError);
        return new SecurityError(
            securityData.GetElementAsString(ElementNames.Security),
            OptionalString(error, ElementNames.Category),
            OptionalString(error, ElementNames.Message),
            OptionalString(error, ElementNames.Subcategory));
    }

    /// <summary>The fieldExceptions of one securityData entry, in order.</summary>
    /// <param name="securityData">One securityData entry.</param>
    /// <returns>The errors, empty when the entry has none.</returns>
    public static IEnumerable<FieldError> FieldErrorsOf(Element securityData)
    {
        if (!securityData.HasElement(ElementNames.FieldExceptions)) yield break;
        string security = securityData.GetElementAsString(ElementNames.Security);
        var exceptions = securityData.GetElement(ElementNames.FieldExceptions);
        for (int index = 0; index < exceptions.NumValues; index++)
        {
            var exception = exceptions.GetValueAsElement(index);
            var info = exception.GetElement(ElementNames.ErrorInfo);
            yield return new FieldError(
                security,
                exception.GetElementAsString(ElementNames.FieldId),
                OptionalString(info, ElementNames.Category),
                OptionalString(info, ElementNames.Message),
                OptionalString(info, ElementNames.Subcategory));
        }
    }

    /// <summary>The calendar date of a value that holds the date parts.</summary>
    /// <param name="value">A Bloomberg DATE or DATETIME value.</param>
    /// <returns>The date, with no zone conversion.</returns>
    public static DateOnly ToDateOnly(Datetime value) =>
        new(value.Year, value.Month, value.DayOfMonth);

    /// <summary>
    /// The value of parent's child field, or null when parent lacks it.
    /// </summary>
    /// <param name="parent">A fieldData element, or one historical data point.</param>
    /// <param name="field">Field mnemonic.</param>
    /// <returns>The value, as <see cref="ToValue"/> returns it.</returns>
    public object? SubelementValue(Element parent, Name field) =>
        parent.HasElement(field) ? ToValue(parent.GetElement(field)) : null;

    /// <summary>The .NET value of a Bloomberg element.</summary>
    /// <param name="element">The element to read.</param>
    /// <returns>
    /// With forceString, a string: "" for null, <see cref="TrimNumber"/> text for a
    /// number, <see cref="SequenceAsJson"/> text for a bulk field. Otherwise null for a
    /// null number, date or time, "" for any other null, the boxed CLR value for a
    /// number, <see cref="DateOnly"/> for a date, <see cref="DateTimeOffset"/> or
    /// <see cref="TimeOnly"/> in timeZone for a date-time or time, a list of
    /// name-to-value rows for a bulk field, and a trimmed string for anything else.
    /// </returns>
    public object? ToValue(Element element)
    {
        var datatype = element.Datatype;
        if (datatype == Schema.Datatype.SEQUENCE)
        {
            return _forceString ? SequenceAsJson(element) : SequenceAsRows(element);
        }

        if (_forceString)
        {
            if (element.IsNull) return "";
            return IsNumeric(datatype)
                ? TrimNumber(NumberText(element))
                : element.GetValueAsString().Trim();
        }

        if (element.IsNull)
        {
            return IsNumeric(datatype) || datatype
                is Schema.Datatype.DATE or Schema.Datatype.DATETIME or Schema.Datatype.TIME
                ? null
                : "";
        }

        if (IsNumeric(datatype)) return element.GetValue();
        return datatype switch
        {
            Schema.Datatype.DATE => ToDateOnly(element.GetValueAsDate()),
            Schema.Datatype.DATETIME or Schema.Datatype.TIME =>
                DateTimeValue(element.GetValueAsDatetime()),
            _ => element.GetValueAsString().Trim(),
        };
    }

    /// <summary>A numeric string in plain notation, trailing zeros dropped.</summary>
    /// <param name="text">A number in plain or exponent form.</param>
    /// <returns>
    /// "7283" for "7283.0", "0.00001" for "1E-05". Text that is not a finite number, or
    /// whose plain form needs more than 28 digits, comes back trimmed.
    /// </returns>
    internal static string TrimNumber(string text)
    {
        string trimmed = text.Trim();
        var match = NumberPattern().Match(trimmed);
        if (!match.Success) return trimmed;
        int parsedExponent = 0;
        if (match.Groups["exponent"].Success
            && !int.TryParse(
                match.Groups["exponent"].ValueSpan,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out parsedExponent))
        {
            return trimmed;
        }

        string fraction = match.Groups["fraction"].Value;
        string digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        string coefficient = digits.TrimEnd('0');
        if (coefficient.Length == 0) return "0";
        long exponent = parsedExponent + digits.Length - coefficient.Length - fraction.Length;
        long wholeDigits = exponent + coefficient.Length;
        if (Math.Max(wholeDigits, 1) + Math.Max(-exponent, 0) > MaxPlainDigits) return trimmed;
        string plain = exponent switch
        {
            >= 0 => coefficient + new string('0', (int)exponent),
            _ when wholeDigits > 0 => coefficient.Insert((int)wholeDigits, "."),
            _ => "0." + new string('0', (int)-wholeDigits) + coefficient,
        };
        return match.Groups["sign"].Value == "-" ? "-" + plain : plain;
    }

    /// <summary>
    /// text as a JSON string literal, escaped as Python's json.dumps does.
    /// </summary>
    /// <param name="text">Any string.</param>
    /// <returns>
    /// The quoted literal. A character outside printable ASCII becomes a lower-case
    /// \uXXXX escape, except \b \t \n \f \r.
    /// </returns>
    internal static string PythonJsonString(string text)
    {
        var literal = new StringBuilder("\"", text.Length + 2);
        foreach (char character in text)
        {
            literal.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' or > '~' => $"\\u{(int)character:x4}",
                _ => character.ToString(),
            });
        }

        return literal.Append('"').ToString();
    }

    /// <summary>
    /// A JSON list with one {name: value} object per subelement of every row.
    /// </summary>
    /// <param name="sequence">A SEQUENCE element, an array of rows.</param>
    /// <returns>
    /// The text Python's json.dumps writes for that list, or "" when it is empty. A
    /// null or single sequence gives "".
    /// </returns>
    internal static string SequenceAsJson(Element sequence)
    {
        List<string> entries = [];
        foreach (var row in Rows(sequence))
        {
            foreach (var subelement in row.Elements)
            {
                string text = IsNumeric(subelement.Datatype)
                    ? TrimNumber(NumberText(subelement))
                    : subelement.GetValueAsString().Trim();
                string name = PythonJsonString(subelement.Name.ToString());
                entries.Add($"{{{name}: {PythonJsonString(text)}}}");
            }
        }

        return entries.Count == 0 ? "" : $"[{string.Join(", ", entries)}]";
    }

    // Python blpapi's values() gives no rows for a null or single sequence.
    private static IEnumerable<Element> Rows(Element sequence)
    {
        if (!sequence.IsArray) yield break;
        for (int index = 0; index < sequence.NumValues; index++)
        {
            yield return sequence.GetValueAsElement(index);
        }
    }

    [GeneratedRegex(
        @"^(?<sign>[+-]?) (?=\.?\d) (?<whole>\d*) (?:\.(?<fraction>\d*))?
          (?:[eE](?<exponent>[+-]?\d+))?$",
        RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex NumberPattern();

    // The Python blp reads the C++ SDK's text, which writes a float as printf %f
    // does. The managed SDK writes the shortest round-trip form instead.
    private static string NumberText(Element element) => element.Datatype
        is Schema.Datatype.FLOAT64 or Schema.Datatype.FLOAT32
        ? element.GetValueAsFloat64().ToString("F6", CultureInfo.InvariantCulture)
        : element.GetValueAsString();

    private static bool IsNumeric(Schema.Datatype datatype) => datatype
        is Schema.Datatype.BOOL or Schema.Datatype.CHAR or Schema.Datatype.INT32
        or Schema.Datatype.INT64 or Schema.Datatype.FLOAT32 or Schema.Datatype.FLOAT64
        or Schema.Datatype.BYTEARRAY or Schema.Datatype.DECIMAL;

    /// <summary>
    /// The string value of parent's child name, or null when parent lacks it.
    /// </summary>
    /// <param name="parent">Any element.</param>
    /// <param name="name">Child element name.</param>
    /// <returns>The string, or null.</returns>
    internal static string? OptionalString(Element parent, Name name) =>
        parent.HasElement(name) ? parent.GetElementAsString(name) : null;

    /// <summary>Each subelement of row, name to value.</summary>
    /// <param name="row">One entry of a SEQUENCE array.</param>
    /// <returns>The values, as <see cref="ToValue"/> returns them.</returns>
    public Dictionary<string, object?> RowValues(Element row) =>
        row.Elements.ToDictionary(subelement => subelement.Name.ToString(), ToValue);

    /// <summary>rows with every name any row holds, as a Python frame has.</summary>
    /// <param name="rows">Rows of name to value.</param>
    /// <returns>New rows. A name a row lacks holds null.</returns>
    public static List<IReadOnlyDictionary<string, object?>> WithEveryName(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var names = rows.SelectMany(row => row.Keys).Distinct().ToList();
        return
        [
            .. rows.Select(row => names.ToDictionary(name => name, row.GetValueOrDefault)),
        ];
    }

    private List<IReadOnlyDictionary<string, object?>> SequenceAsRows(Element sequence) =>
        WithEveryName([.. Rows(sequence).Select(RowValues)]);

    /// <summary>A DATETIME or TIME value, read as UTC, in timeZone.</summary>
    /// <param name="value">The value. Any zone offset it carries is ignored.</param>
    /// <returns>
    /// <see cref="DateOnly"/> without a time, <see cref="DateTimeOffset"/> with a date
    /// and a time. A time alone is dated with this host's date, then gives a
    /// <see cref="TimeOnly"/>, or a <see cref="DateTimeOffset"/> with timeAsDateTime.
    /// </returns>
    private object DateTimeValue(Datetime value)
    {
        var wallClock = DateTime.SpecifyKind(value.ToSystemDateTime(), DateTimeKind.Unspecified);
        // A Python datetime holds whole microseconds, so the Python blp drops the rest.
        wallClock = wallClock.AddTicks(-(wallClock.Ticks % TimeSpan.TicksPerMicrosecond));
        if ((value.Parts & TimeParts) == 0) return DateOnly.FromDateTime(wallClock);
        if (value.HasParts(Datetime.DATE))
        {
            var instant = new DateTimeOffset(wallClock, TimeSpan.Zero);
            return TimeZoneInfo.ConvertTime(instant, _timeZone);
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var stamp = new DateTimeOffset(
            today.ToDateTime(TimeOnly.FromTimeSpan(wallClock.TimeOfDay)), TimeSpan.Zero);
        var zoned = TimeZoneInfo.ConvertTime(stamp, _timeZone);
        return _timeAsDateTime ? zoned : TimeOnly.FromDateTime(zoned.DateTime);
    }
}
