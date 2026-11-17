namespace Blp;

/// <summary>
/// Settings for
/// <see cref="BlpClient.ConnectAsync(BlpClientOptions, CancellationToken)"/>.
/// </summary>
public sealed record BlpClientOptions
{
    /// <summary>
    /// Bloomberg application identity key. Null connects without one.
    /// </summary>
    public string? ApplicationIdentityKey { get; init; }

    /// <summary>
    /// Longest wait for a request's next event. Each partial response restarts it. A
    /// request's own <c>IdleTimeout</c> replaces it.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Longest wait for a request's whole response, which no partial response restarts.
    /// Null sets no limit. A request's own <c>TotalTimeout</c> replaces it.
    /// </summary>
    public TimeSpan? TotalTimeout { get; init; }
}
