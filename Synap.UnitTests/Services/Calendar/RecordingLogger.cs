using Microsoft.Extensions.Logging;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// Captures what an adapter logs. The integration tests have a Serilog RecordingLogSink for the
/// same job; the adapters log through ILogger, so at this level this is its equivalent.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Records { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Records.Add(formatter(state, exception));
        if (exception is not null)
        {
            Records.Add(exception.ToString());
        }
    }

    public bool Mentions(string fragment) => Records.Any(r => r.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
