using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace Synap.IntegrationTests;

/// <summary>
/// Captures what the API logs. Several of this change's behaviours are deliberately invisible in
/// the response - a webhook rejection answers the same bare 401 whatever the reason - so the
/// record is the only thing a test can assert on (specs/reminders "A rejected webhook call is
/// recorded").
///
/// A Serilog sink rather than an ILoggerProvider: Program replaces the logging factory with
/// Serilog's, which ignores providers registered in the container.
/// </summary>
public sealed class RecordingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<string> _records = new();

    public IReadOnlyCollection<string> Records => _records;

    public bool Recorded(string fragment) => _records.Any(r => r.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public void Emit(LogEvent logEvent) => _records.Enqueue(logEvent.RenderMessage());
}
