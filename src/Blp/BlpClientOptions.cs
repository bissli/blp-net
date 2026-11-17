namespace Blp;

/// <summary>Settings for <see cref="BlpClient.ConnectAsync"/>.</summary>
public sealed record BlpClientOptions
{
    /// <summary>
    /// Bloomberg application identity key. Null connects without one.
    /// </summary>
    public string? ApplicationIdentityKey { get; init; }

    /// <summary>
    /// Longest wait for a request's next event. Each partial response restarts it.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
