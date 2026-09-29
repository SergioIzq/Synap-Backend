using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Domain;

/// <summary>
/// How a reminder repeats (assistant-reminders design.md Decision 3): a deliberately small set of
/// schedules stored as a short string, not an iCal RRULE. The LLM and the web app's selector both
/// produce one of these values, and the next occurrence is a switch over them.
/// </summary>
public readonly record struct Recurrence
{
    /// <summary>Fits `monthly:28`; the column is varchar(20).</summary>
    public const int MaxLength = 20;

    /// <summary>Days 29-31 don't exist in every month, so a monthly reminder can't land on one.</summary>
    public const int MaxMonthlyDay = 28;

    public static readonly Error Invalid = Error.Validation(
        "La repetición debe ser \"daily\", \"weekly:<0-6>\" (0 = lunes) o \"monthly:<1-28>\".");

    public static readonly Recurrence Daily = new() { Kind = RecurrenceKind.Daily };

    public RecurrenceKind Kind { get; private init; }

    /// <summary>Weekly: the weekday, 0 = Monday to 6 = Sunday. Monthly: the day of the month, 1 to 28. Unused for daily.</summary>
    public int Value { get; private init; }

    public static Result<Recurrence> Weekly(int weekday)
        => weekday is < 0 or > 6
            ? Result.Failure<Recurrence>(Invalid)
            : Result.Success(new Recurrence { Kind = RecurrenceKind.Weekly, Value = weekday });

    public static Result<Recurrence> Monthly(int dayOfMonth)
        => dayOfMonth is < 1 or > MaxMonthlyDay
            ? Result.Failure<Recurrence>(Invalid)
            : Result.Success(new Recurrence { Kind = RecurrenceKind.Monthly, Value = dayOfMonth });

    /// <summary>A one-off reminder has no recurrence, so null and whitespace are success, not failure.</summary>
    public static Result<Recurrence?> ParseOptional(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Success<Recurrence?>(null);
        }

        var parsed = Parse(raw);
        return parsed.IsFailure ? Result.Failure<Recurrence?>(parsed.Error) : Result.Success<Recurrence?>(parsed.Value);
    }

    public static Result<Recurrence> Parse(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength)
        {
            return Result.Failure<Recurrence>(Invalid);
        }

        if (string.Equals(trimmed, "daily", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success(Daily);
        }

        var separator = trimmed.IndexOf(':');
        if (separator < 0)
        {
            return Result.Failure<Recurrence>(Invalid);
        }

        var prefix = trimmed[..separator];
        // int.TryParse would accept " 1", "+1" and culture separators: only plain digits here.
        var argument = trimmed[(separator + 1)..];
        if (argument.Length == 0 || !argument.All(char.IsAsciiDigit) || !int.TryParse(argument, out var number))
        {
            return Result.Failure<Recurrence>(Invalid);
        }

        if (string.Equals(prefix, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            return Weekly(number);
        }

        return string.Equals(prefix, "monthly", StringComparison.OrdinalIgnoreCase)
            ? Monthly(number)
            : Result.Failure<Recurrence>(Invalid);
    }

    /// <summary>Trusted round-trip from the column, which only ever holds a value <see cref="Parse"/> accepted.</summary>
    public static Recurrence CreateFromDatabase(string raw)
    {
        var parsed = Parse(raw);
        return parsed.IsFailure
            ? throw new ArgumentException($"'{raw}' is not a stored recurrence.", nameof(raw))
            : parsed.Value;
    }

    public override string ToString() => Kind switch
    {
        RecurrenceKind.Daily => "daily",
        RecurrenceKind.Weekly => $"weekly:{Value}",
        _ => $"monthly:{Value}",
    };

    /// <summary>
    /// The first occurrence strictly after <paramref name="fromUtc"/>, keeping the same time of day
    /// in <paramref name="timezone"/> - so a 09:00 reminder stays at 09:00 across a DST switch
    /// instead of drifting to 08:00 or 10:00. An empty timezone resolves in UTC (design.md Context).
    /// </summary>
    public DateTime NextAfter(DateTime fromUtc, string? timezone)
    {
        var zone = UserClock.Zone(timezone);
        var from = UserClock.ToLocal(fromUtc, timezone);

        var next = Kind switch
        {
            RecurrenceKind.Daily => from.AddDays(1),
            RecurrenceKind.Weekly => from.AddDays(DaysUntilNext(from.DayOfWeek)),
            _ => NextMonthly(from),
        };

        return UserClock.ToUtc(next, zone);
    }

    /// <summary>Always 1 to 7: the same weekday means next week, not today.</summary>
    private int DaysUntilNext(DayOfWeek current)
    {
        // 0 = Monday here, while DayOfWeek starts the week on Sunday.
        var target = (DayOfWeek)((Value + 1) % 7);
        var days = ((int)target - (int)current + 7) % 7;
        return days == 0 ? 7 : days;
    }

    /// <summary>
    /// Day <see cref="Value"/> of a following month at the same time of day. Anchored on the day of
    /// the month rather than on "one month later", so a reminder snoozed off its day comes back to it.
    /// </summary>
    private DateTime NextMonthly(DateTime from)
    {
        var candidate = new DateTime(from.Year, from.Month, Value, 0, 0, 0, from.Kind).Add(from.TimeOfDay);
        return candidate > from ? candidate : candidate.AddMonths(1);
    }
}

public enum RecurrenceKind
{
    Daily,
    Weekly,
    Monthly,
}
