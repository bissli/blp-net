using Bloomberglp.Blpapi;
using Blp.Internal;

namespace Blp.ReferenceData;

/// <summary>
/// Sends one <see cref="ReferenceDataRequest"/> and collects its response.
/// </summary>
/// <param name="request">The request to send.</param>
internal sealed class ReferenceDataHandler(ReferenceDataRequest request) : IRequestHandler
{
    private readonly ReferenceDataRequest _request = request;
    private readonly ElementParser _parser =
        new(request.TimeZone, request.ForceString, request.TimeAsDateTime);
    private readonly Dictionary<string, Dictionary<string, object?>> _valuesBySecurity = [];
    private readonly List<SecurityError> _securityErrors = [];
    private readonly List<FieldError> _fieldErrors = [];

    /// <inheritdoc/>
    public Request CreateRequest(Service service)
    {
        var bloombergRequest = service.CreateRequest("ReferenceDataRequest");
        foreach (string ticker in _request.Tickers)
        {
            bloombergRequest.Append(ElementNames.Securities, ticker);
        }

        foreach (string field in _request.Fields)
        {
            bloombergRequest.Append(ElementNames.Fields, field);
        }

        bloombergRequest.Set(ElementNames.UseUtcTime, true);
        FieldOverrides.AppendTo(bloombergRequest, _request.Overrides);
        return bloombergRequest;
    }

    /// <inheritdoc/>
    public void ProcessResponse(Event responseEvent)
    {
        foreach (var message in ElementParser.CheckedMessages(responseEvent))
        {
            var securities = message.GetElement(ElementNames.SecurityData);
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
                foreach (string field in _request.Fields)
                {
                    values[field] = _parser.SubelementValue(fieldData, Name.GetName(field));
                }

                _fieldErrors.AddRange(ElementParser.FieldErrorsOf(securityData));
            }
        }
    }

    /// <summary>The response built from every event processed so far.</summary>
    /// <returns>The response.</returns>
    /// <exception cref="RequestSecurityException">
    /// The request raises security errors and one was received.
    /// </exception>
    /// <exception cref="RequestFieldException">
    /// The request raises field errors and one was received.
    /// </exception>
    public ReferenceDataResponse ToResponse()
    {
        ErrorRaising.ThrowRequested(
            _request.RaiseSecurityError, _request.RaiseFieldError, _securityErrors, _fieldErrors);
        return new(
            _valuesBySecurity.ToDictionary(
                pair => pair.Key, IReadOnlyDictionary<string, object?> (pair) => pair.Value),
            _securityErrors,
            _fieldErrors);
    }
}
