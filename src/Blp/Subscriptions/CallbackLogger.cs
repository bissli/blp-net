using Microsoft.Extensions.Logging;

namespace Blp.Subscriptions;

/// <summary>
/// An <see cref="ILogger"/> that hands each line to a plain callback, for a caller that
/// cannot implement the generic <see cref="ILogger.Log{TState}"/>, such as Python
/// through pythonnet.
/// </summary>
/// <param name="write">
/// Called with the level, the formatted line and the exception the line carries, or
/// null. It runs on the thread that logs, which for a tick error is the SDK's dispatch
/// thread. What it throws reaches the code that logged.
/// </param>
public sealed class CallbackLogger(Action<LogLevel, string, Exception?> write) : ILogger
{
    private readonly Action<LogLevel, string, Exception?> _write = write;

    /// <summary>Scopes are not kept.</summary>
    /// <returns>Null.</returns>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <summary>True for every level except <see cref="LogLevel.None"/>.</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <summary>Hands the formatted line to the callback.</summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _write(logLevel, formatter(state, exception), exception);
    }
}
