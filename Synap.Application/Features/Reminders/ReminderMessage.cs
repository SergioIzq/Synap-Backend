using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Globalization;

namespace Synap.Application.Features.Reminders;

/// <summary>What a button does when pressed (assistant-reminders design.md Decision 4).</summary>
public enum ReminderAction
{
    /// <summary>One-off: done for good. Recurring: this occurrence only, the series carries on.</summary>
    Confirm,
    SnoozeHour,
    SnoozeTomorrow,
    SnoozeNextWeek,
    CancelSeries,
}

/// <summary>
/// A button press coming back from Telegram. <paramref name="OccurrenceDueAt"/> is the due moment
/// the message was delivered for: because a recurring reminder is one recycled row (design.md
/// Decision 2), a second press on the same message would otherwise advance the series twice, so a
/// payload whose moment no longer matches the row is ignored.
/// </summary>
public sealed record ReminderCallback(ReminderAction Action, Guid ReminderId, DateTime OccurrenceDueAt);

/// <summary>
/// Builds the delivered notification: its text, its buttons, and the confirmation the message is
/// edited to afterwards (specs/reminders "A delivered reminder shows its text, its note and its
/// options"). Kept apart from the sender so all of it is testable without HTTP.
/// </summary>
public static class ReminderMessage
{
    /// <summary>Telegram caps callback_data at 64 bytes, which the short keys below stay well inside.</summary>
    public const int MaxCallbackDataBytes = 64;

    private const string Separator = ":";

    public static TelegramMessage For(Reminder reminder, string chatId, string? noteTitle, string? appBaseUrl)
        => new(chatId, Text(reminder, noteTitle, appBaseUrl), Buttons(reminder));

    /// <summary>The reminder itself, then a link to the note when there is one.</summary>
    public static string Text(Reminder reminder, string? noteTitle, string? appBaseUrl)
    {
        var text = $"🔔 <b>{Escape(reminder.Text)}</b>";

        if (reminder.NoteId is { } noteId && !string.IsNullOrWhiteSpace(appBaseUrl))
        {
            var label = string.IsNullOrWhiteSpace(noteTitle) ? "Ver la nota" : Escape(noteTitle);
            text += $"\n\n📄 <a href=\"{appBaseUrl.TrimEnd('/')}/app/notes/{noteId.Value}\">{label}</a>";
        }

        if (reminder.Recurrence is { } recurrence)
        {
            text += $"\n\n🔁 {Describe(recurrence)}";
        }

        return text;
    }

    public static IReadOnlyList<TelegramButton> Buttons(Reminder reminder)
    {
        var occurrence = reminder.DueAt;
        var buttons = new List<TelegramButton>
        {
            new(reminder.IsRecurring ? "✓ Esta vez" : "✓ Hecho", Data(ReminderAction.Confirm, reminder.Id.Value, occurrence)),
            new("⏰ En 1 hora", Data(ReminderAction.SnoozeHour, reminder.Id.Value, occurrence)),
            new("⏰ Mañana", Data(ReminderAction.SnoozeTomorrow, reminder.Id.Value, occurrence)),
            new("⏰ Próxima semana", Data(ReminderAction.SnoozeNextWeek, reminder.Id.Value, occurrence)),
        };

        if (reminder.IsRecurring)
        {
            buttons.Add(new TelegramButton("✗ Cancelar serie", Data(ReminderAction.CancelSeries, reminder.Id.Value, occurrence)));
        }

        return buttons;
    }

    /// <summary>What the message is edited to once the user has acted on it.</summary>
    public static string Outcome(Reminder reminder, ReminderAction action, string? timezone) => action switch
    {
        ReminderAction.Confirm when reminder.IsRecurring => $"✓ Hecho. Te lo recuerdo otra vez el {Moment(reminder.DueAt, timezone)}.",
        ReminderAction.Confirm => "✓ Hecho.",
        ReminderAction.CancelSeries => "✗ Serie cancelada. No volveré a avisarte de esto.",
        _ => $"⏰ Te lo recuerdo el {Moment(reminder.DueAt, timezone)}.",
    };

    /// <summary>A date and time in the user's own timezone, for a message they read there.</summary>
    public static string Moment(DateTime utc, string? timezone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(timezone));
        return local.ToString("dddd d 'de' MMMM 'a las' HH:mm", new CultureInfo("es-ES"));
    }

    public static string Describe(Recurrence recurrence) => recurrence.Kind switch
    {
        RecurrenceKind.Daily => "Todos los días",
        RecurrenceKind.Weekly => $"Todos los {Weekday(recurrence.Value)}",
        _ => $"El día {recurrence.Value} de cada mes",
    };

    public static string Data(ReminderAction action, Guid reminderId, DateTime occurrenceDueAt)
        // Key, id without dashes, and the occurrence as a Unix timestamp: 2 + 32 + 10 plus
        // separators, inside Telegram's 64-byte budget.
        => string.Join(Separator, Key(action), reminderId.ToString("N"), new DateTimeOffset(DateTime.SpecifyKind(occurrenceDueAt, DateTimeKind.Utc)).ToUnixTimeSeconds());

    /// <summary>Null for anything this version didn't produce - a payload from an older deploy included.</summary>
    public static ReminderCallback? Parse(string? data)
    {
        var parts = data?.Split(Separator);
        if (parts is not { Length: 3 }
            || ActionOf(parts[0]) is not { } action
            || !Guid.TryParseExact(parts[1], "N", out var reminderId)
            || !long.TryParse(parts[2], out var occurrence))
        {
            return null;
        }

        return new ReminderCallback(action, reminderId, DateTimeOffset.FromUnixTimeSeconds(occurrence).UtcDateTime);
    }

    private static string Key(ReminderAction action) => action switch
    {
        ReminderAction.Confirm => "ok",
        ReminderAction.SnoozeHour => "s1",
        ReminderAction.SnoozeTomorrow => "s2",
        ReminderAction.SnoozeNextWeek => "s3",
        _ => "xx",
    };

    private static ReminderAction? ActionOf(string key) => key switch
    {
        "ok" => ReminderAction.Confirm,
        "s1" => ReminderAction.SnoozeHour,
        "s2" => ReminderAction.SnoozeTomorrow,
        "s3" => ReminderAction.SnoozeNextWeek,
        "xx" => ReminderAction.CancelSeries,
        _ => null,
    };

    private static string Weekday(int weekday) => weekday switch
    {
        0 => "lunes",
        1 => "martes",
        2 => "miércoles",
        3 => "jueves",
        4 => "viernes",
        5 => "sábados",
        _ => "domingos",
    };

    private static TimeZoneInfo Zone(string? timezone)
        => string.IsNullOrWhiteSpace(timezone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone)
            ? TimeZoneInfo.Utc
            : zone;

    /// <summary>
    /// The reminder's own text is the user's; it must not be able to inject HTML into the message.
    /// Only the three characters Telegram's HTML mode reserves - WebUtility.HtmlEncode would also
    /// escape every accent, and "Volúmenes" would arrive as "Vol&amp;#250;menes".
    /// </summary>
    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
