namespace Blp.Bql;

/// <summary>A Bloomberg BQL query, sent to //blp/bqlsvc.</summary>
/// <param name="Expression">
/// The query text, such as "get(px_last) for(['IBM US Equity'])".
/// </param>
public sealed record BqlRequest(string Expression)
{
    /// <summary>
    /// Longest wait for the next event. Each partial response restarts it. Null takes
    /// <see cref="BlpClientOptions.IdleTimeout"/>.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// Longest wait for the whole response, which no partial response restarts. Null
    /// takes <see cref="BlpClientOptions.TotalTimeout"/>.
    /// </summary>
    public TimeSpan? TotalTimeout { get; init; }
}
