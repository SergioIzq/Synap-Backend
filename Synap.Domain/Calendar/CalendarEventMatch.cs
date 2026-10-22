using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Synap.Domain;

/// <summary>
/// Normaliza títulos de eventos para el filtro `query` de `get_calendar_events`: sin distinguir
/// mayúsculas ni acentos (calendar-integration design.md Decisión 6).
/// </summary>
public static class CalendarEventMatch
{
    /// <summary>Lower case, unaccented, single-spaced, without the article a user puts in front.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.Trim().ToLowerInvariant();
        var stripped = new StringBuilder(lowered.Length);

        foreach (var character in lowered.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        var normalized = Regex.Replace(stripped.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ");
        normalized = Regex.Replace(normalized, @"^(el|la|los|las|mi|mis)\s+", string.Empty);
        return normalized.TrimEnd('.', ',', '!', '?').Trim();
    }
}
