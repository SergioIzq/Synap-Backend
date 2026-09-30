using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Synap.Domain;

/// <summary>
/// Turns the moment as the user said it ("hoy a las 20:20", "el viernes", "en dos semanas") into
/// an instant in UTC (specs/ai-assistant "Reminder moments resolved in the user's timezone").
///
/// This used to be the model's job: `set_reminder` took an ISO 8601 instant in UTC, so setting a
/// reminder was the only assistant action whose arguments had to be *computed* - timezone
/// arithmetic included - rather than copied from what the user had just said. A small model, the
/// kind a free provider tier offers, creates notes and adds tags reliably and does not attempt
/// that: it answers "listo, te recuerdo a las 20:20" and calls nothing. Resolving here makes the
/// tool as cheap to call as the others, and the result testable without a model in the loop.
///
/// Deliberately narrow: it understands the shapes the assistant is told to produce and returns
/// null for everything else, because a moment guessed wrongly and set silently is worse than
/// asking the user when they meant (specs/ai-assistant "Moment that cannot be resolved").
/// </summary>
public static class ReminderWording
{
    /// <summary>A day named without a time means the morning, not midnight.</summary>
    public static readonly TimeSpan DefaultTimeOfDay = TimeSpan.FromHours(9);

    private static readonly string[] Weekdays =
        ["lunes", "martes", "miercoles", "jueves", "viernes", "sabado", "domingo"];

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static readonly Dictionary<string, int> SpelledNumbers = new()
    {
        ["un"] = 1, ["una"] = 1, ["uno"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12,
        ["quince"] = 15, ["veinte"] = 20, ["treinta"] = 30,
    };

    /// <summary>
    /// The instant meant, or null when the wording does not name one. Relative wording resolves to
    /// the nearest match in the future ("el viernes" said on a Friday means the next one); wording
    /// that names an explicit moment resolves to it, past or not.
    /// </summary>
    public static DateTime? Resolve(string? wording, DateTime nowUtc, string? timezone)
    {
        if (string.IsNullOrWhiteSpace(wording))
        {
            return null;
        }

        DateTime utc;

        // An instant already in ISO 8601 is taken as it stands: callers that do know the exact
        // moment - the web app, and a model that still sends one - keep working unchanged. It
        // still has to pass the same rule as every other wording, below.
        if (TryIso(wording, out var iso))
        {
            utc = iso;
        }
        else
        {
            var localNow = UserClock.ToLocal(nowUtc, timezone);
            if (ResolveLocal(Normalize(wording), localNow) is not { } local)
            {
                return null;
            }

            utc = UserClock.ToUtc(local, timezone);
        }

        // Whether the moment is acceptable is not decided here: a wording that names a moment
        // already gone ("hoy a las 8" said at nine in the evening) has been understood perfectly
        // well, and Reminder.Create is what says it has to be in the future - with a message that
        // tells the user that, instead of claiming not to have understood them.
        return utc;
    }

    private static DateTime? ResolveLocal(string text, DateTime localNow)
        => ResolveIn(text, localNow)
           ?? ResolveNamedDay(text, localNow)
           ?? ResolveWeekday(text, localNow)
           ?? ResolveCalendarDate(text, localNow)
           ?? ResolveTimeOnly(text, localNow);

    // ---- "en dos semanas", "en 3 días", "en 45 minutos" ----

    private static DateTime? ResolveIn(string text, DateTime localNow)
    {
        var match = Regex.Match(text, @"^en\s+(?<amount>\d+|[a-z]+)\s+(?<unit>minuto|hora|dia|semana|mes)s?$");
        if (!match.Success || Amount(match.Groups["amount"].Value) is not { } amount)
        {
            return null;
        }

        return match.Groups["unit"].Value switch
        {
            "minuto" => localNow.AddMinutes(amount),
            "hora" => localNow.AddHours(amount),
            // Days and longer keep the time of day rather than the exact offset, so "en dos
            // semanas" said at 20:20 means 20:20, not an instant shifted by a DST change.
            "dia" => localNow.AddDays(amount),
            "semana" => localNow.AddDays(7 * amount),
            "mes" => localNow.AddMonths(amount),
            _ => null,
        };
    }

    // ---- "hoy a las 20:20", "mañana", "pasado mañana a las 8" ----

    private static DateTime? ResolveNamedDay(string text, DateTime localNow)
    {
        var (days, rest) = text switch
        {
            _ when Starts(text, "pasado manana") => (2, Rest(text, "pasado manana")),
            _ when Starts(text, "manana") => (1, Rest(text, "manana")),
            _ when Starts(text, "hoy") => (0, Rest(text, "hoy")),
            _ when Starts(text, "esta noche") => (0, "a las 22"),
            _ when Starts(text, "esta tarde") => (0, "a las 17"),
            _ => (-1, string.Empty),
        };

        if (days < 0)
        {
            return null;
        }

        var timeOfDay = TimeOfDay(rest);
        if (timeOfDay is null && rest.Length > 0)
        {
            // "mañana por si acaso" is not a moment: better to ask than to guess 09:00.
            return null;
        }

        return localNow.Date.AddDays(days).Add(timeOfDay ?? DefaultTimeOfDay);
    }

    // ---- "el viernes", "el lunes a las 9" ----

    private static DateTime? ResolveWeekday(string text, DateTime localNow)
    {
        var index = Array.FindIndex(Weekdays, day => Starts(text, day));
        if (index < 0)
        {
            return null;
        }

        var rest = Rest(text, Weekdays[index]);
        var timeOfDay = TimeOfDay(rest);
        if (timeOfDay is null && rest.Length > 0)
        {
            return null;
        }

        // 0 = Monday, matching Recurrence's weekly:<0-6>.
        var today = ((int)localNow.DayOfWeek + 6) % 7;
        var ahead = (index - today + 7) % 7;
        var candidate = localNow.Date.AddDays(ahead).Add(timeOfDay ?? DefaultTimeOfDay);

        // Said on the same weekday but already past: they mean the next one, not today
        // (specs/ai-assistant "Day already passed this week").
        return candidate <= localNow ? candidate.AddDays(7) : candidate;
    }

    // ---- "el 15 de octubre", "el 15 de octubre a las 8", "15/10" ----

    private static DateTime? ResolveCalendarDate(string text, DateTime localNow)
    {
        var spelled = Regex.Match(text, @"^(?<day>\d{1,2})\s+de\s+(?<month>[a-z]+)(\s+de\s+(?<year>\d{4}))?(?<rest>.*)$");
        var slashed = Regex.Match(text, @"^(?<day>\d{1,2})[/-](?<month>\d{1,2})([/-](?<year>\d{4}))?(?<rest>.*)$");

        int day, month;
        int? year;
        string rest;

        if (spelled.Success)
        {
            month = Array.IndexOf(Months, spelled.Groups["month"].Value) + 1;
            if (month == 0)
            {
                return null;
            }

            day = int.Parse(spelled.Groups["day"].Value, CultureInfo.InvariantCulture);
            year = spelled.Groups["year"].Success ? int.Parse(spelled.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
            rest = spelled.Groups["rest"].Value.Trim();
        }
        else if (slashed.Success)
        {
            day = int.Parse(slashed.Groups["day"].Value, CultureInfo.InvariantCulture);
            month = int.Parse(slashed.Groups["month"].Value, CultureInfo.InvariantCulture);
            year = slashed.Groups["year"].Success ? int.Parse(slashed.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
            rest = slashed.Groups["rest"].Value.Trim();
        }
        else
        {
            return null;
        }

        var timeOfDay = TimeOfDay(rest);
        if (timeOfDay is null && rest.Length > 0)
        {
            return null;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year ?? localNow.Year, month))
        {
            return null;
        }

        var candidate = new DateTime(year ?? localNow.Year, month, day).Add(timeOfDay ?? DefaultTimeOfDay);

        // A date without a year that has already gone by means next year.
        return year is null && candidate <= localNow ? candidate.AddYears(1) : candidate;
    }

    // ---- "a las 20:20", "20:20" ----

    private static DateTime? ResolveTimeOnly(string text, DateTime localNow)
    {
        if (TimeOfDay(text) is not { } timeOfDay)
        {
            return null;
        }

        var candidate = localNow.Date.Add(timeOfDay);
        return candidate <= localNow ? candidate.AddDays(1) : candidate;
    }

    // ---- Shared ----

    /// <summary>"a las 8", "a las 20:20", "a las 8 de la tarde", "20:20"; null when there is no time in it.</summary>
    private static TimeSpan? TimeOfDay(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"^(a\s+las?\s+|a\s+la\s+)?(?<hour>\d{1,2})([:.h](?<minute>\d{2}))?\s*(?<period>de\s+la\s+(manana|tarde|noche)|am|pm)?$");

        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour > 23 || minute > 59)
        {
            return null;
        }

        var period = match.Groups["period"].Value;
        if (hour < 12 && (period.Contains("tarde") || period.Contains("noche") || period == "pm"))
        {
            // "las 8 de la tarde" is 20:00; "las 12 de la noche" stays 12 rather than becoming 24.
            hour += 12;
        }

        return new TimeSpan(hour, minute, 0);
    }

    private static int? Amount(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : SpelledNumbers.TryGetValue(value, out var spelled) ? spelled : null;

    private static bool Starts(string text, string prefix)
        => text == prefix || text.StartsWith(prefix + " ", StringComparison.Ordinal);

    private static string Rest(string text, string prefix) => text[prefix.Length..].Trim();

    private static bool TryIso(string wording, out DateTime utc)
    {
        utc = default;
        return Regex.IsMatch(wording.Trim(), @"^\d{4}-\d{2}-\d{2}[T ]")
               && DateTime.TryParse(
                   wording,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                   out utc);
    }

    /// <summary>
    /// Lower case, without accents and without the filler the assistant puts around a moment, so
    /// "El Viernes", "el viernes" and "viernes" are one thing. Accents go because the model's
    /// spelling of "miércoles" cannot be relied on.
    /// </summary>
    private static string Normalize(string wording)
    {
        var lowered = wording.Trim().ToLowerInvariant();
        var stripped = new StringBuilder(lowered.Length);

        foreach (var character in lowered.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        var text = Regex.Replace(stripped.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ");
        text = Regex.Replace(text, @"^(el|la|este|esta|proximo|proxima|siguiente)\s+", string.Empty);
        return text.TrimEnd('.', ',', '!', '?').Trim();
    }
}
