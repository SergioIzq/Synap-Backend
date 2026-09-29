namespace Synap.Domain;

/// <summary>
/// Converts between a user's wall clock and UTC (assistant-reminders design.md Context). Used by
/// <see cref="Recurrence"/> for the next occurrence and by the snooze options, which both have to
/// mean a time of day - "09:00 tomorrow" - rather than a fixed offset.
///
/// An unknown or missing timezone resolves in UTC rather than throwing: the id is whatever a
/// browser sent us months ago, and a tzdata update can retire it, which must not stop a reminder
/// from being delivered.
/// </summary>
public static class UserClock
{
    public static TimeZoneInfo Zone(string? timezone)
        => string.IsNullOrWhiteSpace(timezone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone)
            ? TimeZoneInfo.Utc
            : zone;

    public static DateTime ToLocal(DateTime utc, string? timezone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(timezone));

    public static DateTime ToUtc(DateTime local, string? timezone) => ToUtc(local, Zone(timezone));

    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Spring forward: this wall clock never happens that day (02:30 where the clocks go
        // 02:00 -> 03:00). Step forward to the first time that does exist.
        while (zone.IsInvalidTime(wallClock))
        {
            wallClock = wallClock.AddMinutes(15);
        }

        // Autumn: an ambiguous wall clock happens twice; ConvertTimeToUtc takes the standard-time
        // (second) one, which is deterministic and good enough for a personal reminder.
        return TimeZoneInfo.ConvertTimeToUtc(wallClock, zone);
    }

    /// <summary>A given time of day, <paramref name="days"/> days after the instant given, in UTC.</summary>
    public static DateTime AtLocalTimeOfDay(DateTime fromUtc, string? timezone, int days, TimeSpan timeOfDay)
    {
        var zone = Zone(timezone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), zone);
        return ToUtc(local.Date.AddDays(days).Add(timeOfDay), zone);
    }
}
