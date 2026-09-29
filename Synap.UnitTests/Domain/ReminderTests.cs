using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Domain;

/// <summary>assistant-reminders tasks 1.3 and 1.4.</summary>
public class ReminderTests
{
    private const string Madrid = "Europe/Madrid";
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tomorrow = new(2026, 7, 11, 7, 0, 0, DateTimeKind.Utc);

    internal static Reminder New(string? recurrence = null, DateTime? dueAt = null)
        => Reminder.Create(NewUserId(), "Renovar el certificado SSL", dueAt ?? Tomorrow, Now, recurrence: recurrence).Value;

    private static UserId NewUserId() => UserId.Create(Guid.NewGuid()).Value;

    // ---- Creation (task 1.3) ----

    [Fact]
    public void A_created_reminder_is_pending_at_its_due_moment()
    {
        var userId = NewUserId();

        var reminder = Reminder.Create(userId, "Renovar el certificado SSL", Tomorrow, Now).Value;

        Assert.Equal(userId, reminder.UserId);
        Assert.Equal("Renovar el certificado SSL", reminder.Text);
        Assert.Equal(Tomorrow, reminder.DueAt);
        Assert.True(reminder.IsPending);
        Assert.False(reminder.IsRecurring);
        Assert.Null(reminder.NoteId);
    }

    [Fact]
    public void Text_is_trimmed()
        => Assert.Equal("Llamar al banco", Reminder.Create(NewUserId(), "  Llamar al banco  ", Tomorrow, Now).Value.Text);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_text_is_rejected(string? text)
    {
        var created = Reminder.Create(NewUserId(), text, Tomorrow, Now);

        Assert.True(created.IsFailure);
        Assert.Equal(Reminder.EmptyText, created.Error);
    }

    [Fact]
    public void A_text_over_the_maximum_length_is_rejected()
    {
        var created = Reminder.Create(NewUserId(), new string('a', Reminder.MaxTextLength + 1), Tomorrow, Now);

        Assert.True(created.IsFailure);
        Assert.Equal(Reminder.TextTooLong, created.Error);
    }

    [Fact]
    public void A_text_at_the_maximum_length_is_accepted()
        => Assert.True(Reminder.Create(NewUserId(), new string('a', Reminder.MaxTextLength), Tomorrow, Now).IsSuccess);

    [Fact]
    public void A_due_moment_in_the_past_is_rejected()
    {
        var created = Reminder.Create(NewUserId(), "Llamar al banco", Now.AddMinutes(-1), Now);

        Assert.True(created.IsFailure);
        Assert.Equal(Reminder.DueInThePast, created.Error);
    }

    [Fact]
    public void The_current_moment_is_not_in_the_future()
    {
        var created = Reminder.Create(NewUserId(), "Llamar al banco", Now, Now);

        Assert.True(created.IsFailure);
        Assert.Equal(Reminder.DueInThePast, created.Error);
    }

    [Fact]
    public void An_unsupported_recurrence_is_rejected()
    {
        var created = Reminder.Create(NewUserId(), "Llamar al banco", Tomorrow, Now, recurrence: "every weekday");

        Assert.True(created.IsFailure);
        Assert.Equal(Recurrence.Invalid, created.Error);
    }

    [Fact]
    public void A_reminder_can_be_linked_to_a_note()
    {
        var noteId = NoteId.Create(Guid.NewGuid()).Value;

        var reminder = Reminder.Create(NewUserId(), "Revisar esto", Tomorrow, Now, noteId).Value;

        Assert.Equal(noteId, reminder.NoteId);
    }

    [Fact]
    public void A_due_moment_is_stored_to_whole_seconds()
    {
        // The delivered message's buttons carry the occurrence as a Unix timestamp; sub-second
        // precision would make the callback's guard never match.
        var reminder = Reminder.Create(NewUserId(), "Llamar al banco", Tomorrow.AddMilliseconds(654), Now).Value;

        Assert.Equal(Tomorrow, reminder.DueAt);
        Assert.Equal(0, reminder.DueAt.Millisecond);
    }

    [Fact]
    public void A_snoozed_moment_is_also_stored_to_whole_seconds()
    {
        var reminder = New();

        reminder.Snooze(Tomorrow.AddHours(1).AddMilliseconds(321));

        Assert.Equal(Tomorrow.AddHours(1), reminder.DueAt);
    }

    [Fact]
    public void A_due_moment_without_a_kind_is_read_as_utc()
    {
        // A DateTime parsed from JSON arrives Unspecified; comparing it against a Utc one would
        // otherwise compare wall clocks.
        var reminder = Reminder.Create(NewUserId(), "Llamar al banco", new DateTime(2026, 7, 11, 7, 0, 0), Now).Value;

        Assert.Equal(DateTimeKind.Utc, reminder.DueAt.Kind);
        Assert.Equal(Tomorrow, reminder.DueAt);
    }

    // ---- Delivery lifecycle (task 1.4) ----

    [Fact]
    public void A_delivered_reminder_is_no_longer_pending()
    {
        var reminder = New();

        reminder.MarkSent(Tomorrow);

        Assert.False(reminder.IsPending);
        Assert.Equal(Tomorrow, reminder.SentAt);
    }

    [Fact]
    public void A_confirmed_one_off_reminder_is_never_pending_again()
    {
        var reminder = New();
        reminder.MarkSent(Tomorrow);

        reminder.Confirm(Tomorrow.AddMinutes(1), Madrid);

        Assert.False(reminder.IsPending);
        Assert.NotNull(reminder.DismissedAt);
        Assert.Equal(Tomorrow, reminder.DueAt);
    }

    [Fact]
    public void A_confirmed_daily_reminder_comes_back_the_next_day()
    {
        var reminder = New("daily");
        reminder.MarkSent(Tomorrow);

        reminder.Confirm(Tomorrow.AddMinutes(1), Madrid);

        Assert.True(reminder.IsPending);
        Assert.Equal(Tomorrow.AddDays(1), reminder.DueAt);
        Assert.Null(reminder.SentAt);
        Assert.Null(reminder.DismissedAt);
        Assert.True(reminder.IsRecurring);
    }

    [Fact]
    public void A_confirmed_weekly_reminder_comes_back_the_next_matching_weekday()
    {
        // 2026-07-13 is a Monday.
        var monday = new DateTime(2026, 7, 13, 16, 0, 0, DateTimeKind.Utc);
        var reminder = New("weekly:0", monday);
        reminder.MarkSent(monday);

        reminder.Confirm(monday.AddMinutes(1), Madrid);

        Assert.Equal(monday.AddDays(7), reminder.DueAt);
    }

    [Fact]
    public void A_confirmed_monthly_reminder_comes_back_the_following_month()
    {
        var fifteenth = new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc);
        var reminder = New("monthly:15", fifteenth);
        reminder.MarkSent(fifteenth);

        reminder.Confirm(fifteenth.AddMinutes(1), Madrid);

        Assert.Equal(new DateTime(2026, 8, 15, 7, 0, 0, DateTimeKind.Utc), reminder.DueAt);
    }

    [Fact]
    public void A_snoozed_reminder_is_pending_again_at_the_new_moment()
    {
        var reminder = New();
        reminder.MarkSent(Tomorrow);

        reminder.Snooze(Tomorrow.AddHours(1));

        Assert.True(reminder.IsPending);
        Assert.Equal(Tomorrow.AddHours(1), reminder.DueAt);
        Assert.Null(reminder.SentAt);
    }

    [Fact]
    public void A_snoozed_recurring_reminder_is_still_recurring()
    {
        var reminder = New("daily");
        reminder.MarkSent(Tomorrow);

        reminder.Snooze(Tomorrow.AddDays(7));

        Assert.True(reminder.IsRecurring);
        Assert.Equal(RecurrenceKind.Daily, reminder.Recurrence!.Value.Kind);
    }

    [Fact]
    public void A_cancelled_series_is_never_pending_again()
    {
        var reminder = New("daily");
        reminder.MarkSent(Tomorrow);

        reminder.CancelSeries();

        Assert.False(reminder.IsPending);
        Assert.False(reminder.IsRecurring);
        Assert.NotNull(reminder.DismissedAt);
    }

    [Fact]
    public void A_series_cancelled_before_delivery_is_still_never_delivered()
    {
        // The poller looks for rows with no SentAt, so cancelling one that was never sent has to
        // take it out of that set too.
        var reminder = New("daily");

        reminder.CancelSeries();

        Assert.False(reminder.IsPending);
        Assert.NotNull(reminder.SentAt);
    }

    [Fact]
    public void An_edit_moves_the_reminder_and_makes_it_pending_again()
    {
        var reminder = New();
        reminder.MarkSent(Tomorrow);

        var edited = reminder.Edit("Llamar al banco", Tomorrow.AddDays(2), Now, "weekly:2");

        Assert.True(edited.IsSuccess);
        Assert.Equal("Llamar al banco", reminder.Text);
        Assert.Equal(Tomorrow.AddDays(2), reminder.DueAt);
        Assert.Equal(RecurrenceKind.Weekly, reminder.Recurrence!.Value.Kind);
        Assert.True(reminder.IsPending);
    }

    [Fact]
    public void An_edit_to_a_past_moment_is_rejected_and_changes_nothing()
    {
        var reminder = New("daily");

        var edited = reminder.Edit("Otro texto", Now.AddMinutes(-1), Now, "daily");

        Assert.True(edited.IsFailure);
        Assert.Equal(Reminder.DueInThePast, edited.Error);
        Assert.Equal("Renovar el certificado SSL", reminder.Text);
        Assert.Equal(Tomorrow, reminder.DueAt);
    }

    [Fact]
    public void An_edit_with_an_invalid_recurrence_is_rejected_and_changes_nothing()
    {
        var reminder = New();

        var edited = reminder.Edit("Otro texto", Tomorrow.AddDays(1), Now, "monthly:31");

        Assert.True(edited.IsFailure);
        Assert.Equal("Renovar el certificado SSL", reminder.Text);
        Assert.Equal(Tomorrow, reminder.DueAt);
    }

    [Fact]
    public void An_edit_can_drop_the_recurrence()
    {
        var reminder = New("daily");

        reminder.Edit("Renovar el certificado SSL", Tomorrow.AddDays(1), Now, null);

        Assert.False(reminder.IsRecurring);
    }

    [Fact]
    public void Unlinking_a_deleted_note_leaves_the_reminder_pending()
    {
        var reminder = Reminder.Create(NewUserId(), "Revisar esto", Tomorrow, Now, NoteId.Create(Guid.NewGuid()).Value).Value;

        reminder.UnlinkNote();

        Assert.Null(reminder.NoteId);
        Assert.True(reminder.IsPending);
    }
}
