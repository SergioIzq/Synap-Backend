using System.Collections.Concurrent;

namespace Synap.Application.Features.Reminders;

/// <summary>
/// Remembers which reminders have already been reported as undeliverable, so the poller says it
/// once instead of every minute for as long as the reminder stays pending
/// (specs/reminders "Due with no chat linked").
///
/// The condition - no linked chat - is identical on every sweep, which is why the code used to
/// skip these in complete silence. Silence is what made a reminder that never arrives
/// indistinguishable from one that was never created.
///
/// A singleton, because the delivery service is built fresh for each tick. What it holds is a set
/// of ids, dropped wholesale once it grows past a size no personal vault reaches; the cost of
/// that is one repeated record, not a leak.
/// </summary>
public sealed class WithheldReminderRecorder
{
    private const int MaxRemembered = 1_000;

    private readonly ConcurrentDictionary<Guid, byte> _recorded = new();

    /// <summary>True the first time this reminder is withheld, false while it stays remembered.</summary>
    public bool ShouldRecord(Guid reminderId)
    {
        if (_recorded.Count >= MaxRemembered)
        {
            _recorded.Clear();
        }

        return _recorded.TryAdd(reminderId, 0);
    }

    /// <summary>Delivered or cancelled: the next time it is withheld is worth saying again.</summary>
    public void Forget(Guid reminderId) => _recorded.TryRemove(reminderId, out _);
}
