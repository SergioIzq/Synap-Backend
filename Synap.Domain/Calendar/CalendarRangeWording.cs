using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Synap.Domain;

/// <summary>
/// Convierte el intervalo tal como lo dice el usuario ("mañana", "esta semana", "el viernes",
/// "próximos 10 días") en días locales completos y su equivalente UTC (calendar-integration
/// design.md Decisión 6). Como con <see cref="ReminderWording"/>, el modelo no hace aritmética de
/// fechas; los días sueltos los reconoce el propio <see cref="ReminderWording.ResolveDay"/>.
///
/// Si no se entiende, se usan los próximos <see cref="DefaultDays"/> días y
/// <see cref="CalendarRange.Understood"/> es false.
/// </summary>
public static class CalendarRangeWording
{
    public const int DefaultDays = 30;

    /// <summary>El intervalo más largo que se lee de una vez (specs: "un intervalo que fija el sistema").</summary>
    public const int MaxDays = 60;

    public static CalendarRange Resolve(string? wording, DateTime nowUtc, string? timezone)
    {
        var today = DateOnly.FromDateTime(UserClock.ToLocal(nowUtc, timezone));
        var text = Fold(wording);

        if (text.Length > 0 && ResolveRange(text, today) is { } range)
        {
            return Build(range.First, range.Days, timezone, understood: true);
        }

        if (text.Length > 0 && ReminderWording.ResolveDay(wording, today.ToDateTime(TimeOnly.MinValue)) is { } day)
        {
            return Build(day, 1, timezone, understood: true);
        }

        return Build(today, DefaultDays, timezone, understood: false);
    }

    private static (DateOnly First, int Days)? ResolveRange(string text, DateOnly today)
    {
        // Lunes = 0.
        var weekday = ((int)today.DayOfWeek + 6) % 7;
        var nextMonday = today.AddDays(7 - weekday);

        if (Regex.IsMatch(text, @"^(esta|toda esta|lo que queda de)( la)? semana$"))
        {
            return (today, nextMonday.DayNumber - today.DayNumber);
        }

        if (Regex.IsMatch(text, @"^(la )?(semana que viene|proxima semana|semana proxima|siguiente semana)$"))
        {
            return (nextMonday, 7);
        }

        if (Regex.IsMatch(text, @"^(este|todo este|lo que queda de)( el)? mes$"))
        {
            var firstOfNext = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
            return (today, firstOfNext.DayNumber - today.DayNumber);
        }

        var next = Regex.Match(text, @"^(en )?(los )?(proximos|siguientes) (?<n>\d+) dias$");
        if (next.Success && int.TryParse(next.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return (today, Math.Clamp(n, 1, MaxDays));
        }

        return null;
    }

    /// <summary>De la medianoche local del primer día a la del día siguiente al último, cada una con su desfase.</summary>
    private static CalendarRange Build(DateOnly first, int days, string? timezone, bool understood)
    {
        var fromUtc = UserClock.ToUtc(first.ToDateTime(TimeOnly.MinValue), timezone);
        var toUtc = UserClock.ToUtc(first.AddDays(days).ToDateTime(TimeOnly.MinValue), timezone);
        return new CalendarRange(fromUtc, toUtc, first, days, understood);
    }

    /// <summary>Minúsculas, sin acentos ni espacios repetidos ni puntuación final.</summary>
    private static string Fold(string? wording)
    {
        if (string.IsNullOrWhiteSpace(wording))
        {
            return string.Empty;
        }

        var lowered = wording.Trim().ToLowerInvariant();
        var stripped = new StringBuilder(lowered.Length);
        foreach (var character in lowered.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        return Regex.Replace(stripped.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ")
            .TrimEnd('.', ',', '!', '?').Trim();
    }
}

/// <summary>
/// Días locales completos: de la medianoche de <see cref="FirstDay"/> a la del día siguiente al
/// último, en UTC. <see cref="Understood"/> es false cuando se usó el intervalo por defecto.
/// </summary>
public sealed record CalendarRange(DateTime FromUtc, DateTime ToUtc, DateOnly FirstDay, int Days, bool Understood);
