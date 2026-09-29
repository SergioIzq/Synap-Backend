using SergioIzq.Domain.Kernel.Abstractions;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

/// <summary>
/// A text the user wants to be told about at a given moment (specs/reminders), optionally tied to
/// one of their notes and optionally repeating. A recurring reminder is a single recycled row
/// (assistant-reminders design.md Decision 2): confirming an occurrence moves <see cref="DueAt"/>
/// forward instead of writing a new row, so there is no history of past occurrences.
/// </summary>
[Table("reminders")]
public sealed class Reminder : AbsEntity<ReminderId>
{
    public const int MaxTextLength = 500;

    public static readonly Error EmptyText = Error.Validation("El recordatorio no puede estar vacío.");
    public static readonly Error TextTooLong = Error.Validation($"El recordatorio no puede superar los {MaxTextLength} caracteres.");
    public static readonly Error DueInThePast = Error.Validation("El momento del recordatorio tiene que estar en el futuro.");
    public static readonly Error NotFound = Error.NotFound("Recordatorio no encontrado.");

    private Reminder() : base(ReminderId.Create(Guid.NewGuid()).Value)
    {
    }

    private Reminder(ReminderId id, UserId userId, string text, DateTime dueAtUtc, NoteId? noteId, Recurrence? recurrence) : base(id)
    {
        UserId = userId;
        Text = text;
        DueAt = dueAtUtc;
        NoteId = noteId;
        Recurrence = recurrence;
    }

    public UserId UserId { get; private set; }
    public string Text { get; private set; } = null!;

    /// <summary>Always UTC: the poller compares it against the current instant (design.md Decision 6).</summary>
    public DateTime DueAt { get; private set; }

    /// <summary>Null once the linked note is deleted - the reminder outlives it (specs/reminders).</summary>
    public NoteId? NoteId { get; private set; }

    /// <summary>Null for a one-off reminder.</summary>
    public Recurrence? Recurrence { get; private set; }

    /// <summary>When the current occurrence was delivered; null while it is still waiting.</summary>
    public DateTime? SentAt { get; private set; }

    /// <summary>When the user confirmed the current occurrence, or when the series was cancelled.</summary>
    public DateTime? DismissedAt { get; private set; }

    public bool IsRecurring => Recurrence is not null;

    /// <summary>What the poller looks for: due, not yet delivered, not dismissed.</summary>
    public bool IsPending => SentAt is null && DismissedAt is null;

    public static Result<Reminder> Create(UserId userId, string? text, DateTime dueAtUtc, DateTime nowUtc, NoteId? noteId = null, string? recurrence = null)
    {
        var validated = Validate(text, dueAtUtc, nowUtc);
        if (validated.IsFailure)
        {
            return Result.Failure<Reminder>(validated.Error);
        }

        // Fully qualified: the Recurrence property shadows the type name inside this class.
        var parsed = Synap.Domain.Recurrence.ParseOptional(recurrence);
        return parsed.IsFailure
            ? Result.Failure<Reminder>(parsed.Error)
            : Result.Success(new Reminder(
                ReminderId.Create(Guid.NewGuid()).Value, userId, validated.Value, AsDueMoment(dueAtUtc), noteId, parsed.Value));
    }

    /// <summary>Same validation as creation: an edit can't leave a reminder a creation would have refused.</summary>
    public Result Edit(string? text, DateTime dueAtUtc, DateTime nowUtc, string? recurrence)
    {
        var validated = Validate(text, dueAtUtc, nowUtc);
        if (validated.IsFailure)
        {
            return validated;
        }

        var parsed = Synap.Domain.Recurrence.ParseOptional(recurrence);
        if (parsed.IsFailure)
        {
            return Result.Failure(parsed.Error);
        }

        Text = validated.Value;
        Recurrence = parsed.Value;
        Reschedule(dueAtUtc);
        return Result.Success();
    }

    public void MarkSent(DateTime nowUtc) => SentAt = AsUtc(nowUtc);

    /// <summary>
    /// The user confirmed the delivered occurrence. A one-off reminder is done for good; a
    /// recurring one comes back at its next occurrence, computed in the user's own timezone so a
    /// 09:00 reminder stays at 09:00 across a DST switch.
    /// </summary>
    public void Confirm(DateTime nowUtc, string? timezone)
    {
        DismissedAt = AsUtc(nowUtc);

        if (Recurrence is { } recurrence)
        {
            Reschedule(recurrence.NextAfter(DueAt, timezone));
        }
    }

    /// <summary>Postponed to one of the three fixed moments (specs/reminders); the recurrence is untouched.</summary>
    public void Snooze(DateTime untilUtc) => Reschedule(untilUtc);

    /// <summary>Stops a recurring series for good. A cancelled reminder is never pending again.</summary>
    public void CancelSeries()
    {
        DismissedAt = DateTime.UtcNow;
        Recurrence = null;
        SentAt ??= DateTime.UtcNow;
    }

    /// <summary>The linked note was deleted; the reminder stays and is still delivered (specs/reminders).</summary>
    public void UnlinkNote() => NoteId = null;

    private void Reschedule(DateTime dueAtUtc)
    {
        DueAt = AsDueMoment(dueAtUtc);
        SentAt = null;
        DismissedAt = null;
    }

    /// <summary>Trimmed text, or why the reminder can't be stored.</summary>
    private static Result<string> Validate(string? text, DateTime dueAtUtc, DateTime nowUtc)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Result.Failure<string>(EmptyText);
        }

        if (trimmed.Length > MaxTextLength)
        {
            return Result.Failure<string>(TextTooLong);
        }

        return AsUtc(dueAtUtc) <= AsUtc(nowUtc) ? Result.Failure<string>(DueInThePast) : Result.Success(trimmed);
    }

    /// <summary>
    /// Npgsql hands back a timestamp as Unspecified, and a caller can pass an Unspecified DateTime
    /// parsed from JSON: comparing those against a Utc one throws nothing and silently compares
    /// wall clocks, so the kind is pinned here rather than trusted.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Whole seconds. A delivered reminder's button carries its occurrence as a Unix timestamp, and
    /// the callback is only acted on when that still matches <see cref="DueAt"/> (design.md
    /// Decision 2) - which it never would if the stored value kept the microseconds Postgres
    /// accepts. Reminders are minute-precision anyway, so nothing of value is lost.
    /// </summary>
    private static DateTime AsDueMoment(DateTime value)
    {
        var utc = AsUtc(value);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
