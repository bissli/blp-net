using Bloomberglp.Blpapi;

namespace Blp.Internal;

/// <summary>Writes field overrides into a Bloomberg request.</summary>
internal static class FieldOverrides
{
    /// <summary>Appends one overrides entry per pair to request.</summary>
    /// <param name="request">A request whose schema has an overrides array.</param>
    /// <param name="overrides">
    /// Field id to override value. Empty appends nothing.
    /// </param>
    public static void AppendTo(Request request, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (fieldId, value) in overrides)
        {
            var entry = request.GetElement(ElementNames.Overrides).AppendElement();
            entry.SetElement(ElementNames.FieldId, fieldId);
            entry.SetElement(ElementNames.Value, value);
        }
    }
}
