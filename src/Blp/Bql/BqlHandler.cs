using System.Globalization;
using System.Text.Json;
using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.Bql;

/// <summary>Sends one <see cref="BqlRequest"/> and collects its response.</summary>
/// <param name="request">The request to send.</param>
internal sealed class BqlHandler(BqlRequest request) : IRequestHandler
{
    private readonly BqlRequest _request = request;
    private readonly Dictionary<string, List<IReadOnlyDictionary<string, object?>>> _rowsByResult =
        [];
    private readonly List<string> _errors = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("sendQuery");
        bloombergRequest.Set(ElementNames.Expression, _request.Expression);
        bloombergRequest
            .GetElement(ElementNames.ClientContext)
            .SetElement(ElementNames.AppName, "EXCEL");
        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            using var document = JsonDocument.Parse(message.AsElement.GetValueAsString());
            var root = document.RootElement;
            var exceptions = Child(root, "responseExceptions");
            if (exceptions.ValueKind == JsonValueKind.Array && exceptions.GetArrayLength() > 0)
            {
                foreach (var exception in exceptions.EnumerateArray())
                {
                    _errors.Add(
                        TextOf(exception, "message")
                        ?? TextOf(exception, "internalMessage")
                        ?? "Unknown BQL error");
                }

                continue;
            }

            var results = Child(root, "results");
            if (results.ValueKind != JsonValueKind.Object) continue;

            foreach (var result in results.EnumerateObject())
            {
                if (!_rowsByResult.TryGetValue(result.Name, out var rows))
                {
                    _rowsByResult[result.Name] = rows = [];
                }

                var columns = new List<(string Name, List<object?> Values)>
                {
                    ("ID", ColumnValues(Child(result.Value, "idColumn"))),
                    (result.Name, ColumnValues(Child(result.Value, "valuesColumn"))),
                };
                var secondaryColumns = Child(result.Value, "secondaryColumns");
                if (secondaryColumns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var column in secondaryColumns.EnumerateArray())
                    {
                        columns.Add((TextOf(column, "name") ?? "", ColumnValues(column)));
                    }
                }

                for (int index = 0; index < columns[0].Values.Count; index++)
                {
                    Dictionary<string, object?> row = [];
                    foreach (var (name, values) in columns)
                    {
                        row[name] = index < values.Count ? values[index] : null;
                    }

                    rows.Add(row);
                }
            }
        }
    }

    /// <summary>The response built from every event processed so far.</summary>
    /// <returns>The response.</returns>
    public BqlResponse ToResponse() =>
        new(
            _rowsByResult.ToDictionary(
                pair => pair.Key,
                IReadOnlyList<IReadOnlyDictionary<string, object?>> (pair) => pair.Value),
            _errors);

    private static JsonElement Child(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var child)
            ? child
            : default;

    private static string? TextOf(JsonElement parent, string property) =>
        Child(parent, property) is { ValueKind: JsonValueKind.String } text
        && text.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static List<object?> ColumnValues(JsonElement column)
    {
        string? type = TextOf(column, "type");
        var array = Child(column, "values");
        List<object?> values = [];
        if (array.ValueKind != JsonValueKind.Array) return values;

        foreach (var value in array.EnumerateArray())
        {
            if (type == "DATE"
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: >= 10 } text
                && DateOnly.TryParseExact(
                    text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                values.Add(date);
                continue;
            }

            values.Add(value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when type == "INT" && value.TryGetInt64(out long whole) =>
                    whole,
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when value.GetString() == "NaN" => null,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => null,
                _ => value.GetRawText(),
            });
        }

        return values;
    }
}
