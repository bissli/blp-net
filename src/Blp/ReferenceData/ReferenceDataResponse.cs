namespace Blp.ReferenceData;

/// <summary>The answer to a <see cref="ReferenceDataRequest"/>.</summary>
/// <param name="Values">
/// Security, as Bloomberg echoed it, to field to value. Every requested field is
/// present, null where Bloomberg sent none. A rejected security is absent.
/// </param>
/// <param name="SecurityErrors">Securities Bloomberg rejected.</param>
/// <param name="FieldErrors">Fields Bloomberg rejected, per security.</param>
public sealed record ReferenceDataResponse(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Values,
    IReadOnlyList<SecurityError> SecurityErrors,
    IReadOnlyList<FieldError> FieldErrors);
