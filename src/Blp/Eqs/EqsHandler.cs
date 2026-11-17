using System.Globalization;
using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.Eqs;

/// <summary>Sends one <see cref="EqsRequest"/> and collects its response.</summary>
/// <param name="request">The request to send.</param>
internal sealed class EqsHandler(EqsRequest request) : IRequestHandler
{
    private readonly EqsRequest _request = request;
    private readonly ElementParser _parser =
        new(request.TimeZone, forceString: false, timeAsDateTime: false);
    private readonly Dictionary<string, Dictionary<string, object?>> _valuesBySecurity = [];
    private readonly List<SecurityError> _securityErrors = [];
    private readonly List<FieldError> _fieldErrors = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("BeqsRequest");
        bloombergRequest.Set(ElementNames.ScreenName, _request.ScreenName);
        bloombergRequest.Set(
            ElementNames.ScreenType, ElementNames.BloombergName(_request.ScreenType));
        if (!string.IsNullOrEmpty(_request.Group))
        {
            bloombergRequest.Set(ElementNames.Group, _request.Group);
        }

        if (_request.AsOf is { } asOf)
        {
            FieldOverrides.AppendTo(
                bloombergRequest,
                new Dictionary<string, string>
                {
                    ["PiTDate"] = asOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                });
        }

        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            var securities = message
                .GetElement(ElementNames.Data)
                .GetElement(ElementNames.SecurityData);
            for (int index = 0; index < securities.NumValues; index++)
            {
                var securityData = securities.GetValueAsElement(index);
                if (ElementParser.SecurityErrorOf(securityData) is { } error)
                {
                    _securityErrors.Add(error);
                    continue;
                }

                string security = securityData.GetElementAsString(ElementNames.Security);
                if (!_valuesBySecurity.TryGetValue(security, out var values))
                {
                    _valuesBySecurity[security] = values = [];
                }

                var fieldData = securityData.GetElement(ElementNames.FieldData);
                foreach (var field in fieldData.Elements)
                {
                    values[field.Name.ToString()] = _parser.SubelementValue(fieldData, field.Name);
                }

                _fieldErrors.AddRange(ElementParser.FieldErrorsOf(securityData));
            }
        }
    }

    /// <summary>The response built from every event processed so far.</summary>
    /// <returns>The response.</returns>
    public EqsResponse ToResponse() =>
        new(
            _valuesBySecurity.ToDictionary(
                pair => pair.Key, IReadOnlyDictionary<string, object?> (pair) => pair.Value),
            _securityErrors,
            _fieldErrors);
}
