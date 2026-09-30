using System.Collections.Concurrent;

namespace Synap.Api.Telegram;

/// <summary>
/// Records why a call to the Telegram webhook was rejected, at most once per reason per window
/// (specs/reminders "A rejected webhook call is recorded").
///
/// The endpoint is anonymous and on the open internet, so anything can post to it: recording every
/// rejection would let a scanner turn the log into a denial of service. Nothing is lost by
/// sampling, because the reason is a configuration state - it is identical on every call until
/// somebody changes the configuration.
///
/// A service rather than a static: the window has to be closable in tests, and a controller with
/// process-wide mutable state cannot be exercised twice in the same run.
/// </summary>
public sealed class WebhookRejectionRecorder(TimeSpan window)
{
    private readonly ConcurrentDictionary<string, DateTime> _lastRecorded = new();

    public WebhookRejectionRecorder()
        : this(TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>True when this rejection was recorded, false when an identical one just was.</summary>
    public bool ShouldRecord(string reason, DateTime nowUtc)
    {
        var recorded = false;

        _lastRecorded.AddOrUpdate(
            reason,
            _ =>
            {
                recorded = true;
                return nowUtc;
            },
            (_, last) =>
            {
                if (nowUtc - last < window)
                {
                    return last;
                }

                recorded = true;
                return nowUtc;
            });

        return recorded;
    }
}
