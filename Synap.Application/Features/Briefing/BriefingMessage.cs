using Synap.Domain;
using System.Globalization;
using System.Text;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// Turns a <see cref="BriefingContent"/> into the message the user reads (daily-briefing design.md
/// Decision 5). Pure: no clock of its own, no Telegram, no database - every promise the spec makes
/// about what a briefing says is testable here.
///
/// HTML like the reminder's, because the sender posts with parse_mode=HTML.
/// </summary>
public static class BriefingMessage
{
    /// <summary>What a briefing the user asked for says when there is nothing to report.</summary>
    public const string NothingToReport = "☀️ Hoy no tienes nada pendiente: ni recordatorios, ni notas sin etiquetar, ni hilos abiertos.";

    private static readonly CultureInfo Spanish = new("es-ES");

    /// <summary>
    /// The briefing's text, or null when there is nothing to report. Null is a distinct outcome
    /// rather than an empty string on purpose: the automatic briefing stays silent on such a day,
    /// and only the caller knows whether the user asked for this one (specs/briefing "Nothing to
    /// report means nothing is sent unasked").
    /// </summary>
    public static string? Text(BriefingContent content, string? timezone)
    {
        if (content.IsEmpty)
        {
            return null;
        }

        var text = new StringBuilder("☀️ <b>Buenos días.</b> Esto es lo que tienes hoy:");

        Section(text, "⏰", "Recordatorios de hoy", content.RemindersToday, r =>
        {
            var at = UserClock.ToLocal(r.DueAt, timezone).ToString("HH:mm", Spanish);
            // Parentheses, not another "·": the line already uses it as a bullet and as the
            // separator after the time, and a third meaning makes it unreadable.
            var note = string.IsNullOrWhiteSpace(r.NoteTitle) ? string.Empty : $" ({Escape(r.NoteTitle)})";
            return $"{at} · {Escape(r.Text)}{note}";
        });

        Section(text, "📌", "Notas sin etiquetar", content.UntaggedNotes, NoteLine);
        Section(text, "🔎", "Hilos abiertos", content.OpenThreads, NoteLine);

        return text.ToString();
    }

    private static string NoteLine(BriefingNote note) => Escape(NoteDisplay.Label(note.Title, note.Content));

    /// <summary>
    /// One section, or nothing at all when it is empty - an empty heading is noise the user has to
    /// read past every morning (specs/briefing "An empty section is left out"). A capped section
    /// says how many it stands for, so the number is never silently lost.
    /// </summary>
    private static void Section<T>(StringBuilder text, string icon, string heading, BriefingSection<T> section, Func<T, string> line)
    {
        if (section.IsEmpty)
        {
            return;
        }

        text.Append($"\n\n{icon} <b>{heading}</b> ({section.Total})");
        foreach (var item in section.Items)
        {
            text.Append($"\n· {line(item)}");
        }

        if (section.IsTruncated)
        {
            var rest = section.Total - section.Items.Count;
            text.Append($"\n<i>… y {rest} más</i>");
        }
    }

    /// <summary>The three characters Telegram's HTML mode treats as markup.</summary>
    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
