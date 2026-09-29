using Synap.Application.Features.Reminders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.Text;

namespace Synap.UnitTests.Features.Reminders;

/// <summary>assistant-reminders task 5.3.</summary>
public class ReminderMessageTests
{
    private const string Madrid = "Europe/Madrid";
    private const string BaseUrl = "https://synap.sergioizq.com";
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static Reminder New(string? recurrence = null, NoteId? noteId = null, string text = "Renovar el certificado SSL")
        => Reminder.Create(UserId.Create(Guid.NewGuid()).Value, text, Now.AddDays(1), Now, noteId, recurrence).Value;

    // ---- Keyboards ----

    [Fact]
    public void A_one_off_reminder_offers_done_and_the_three_snoozes()
    {
        var buttons = ReminderMessage.Buttons(New());

        Assert.Equal(["✓ Hecho", "⏰ En 1 hora", "⏰ Mañana", "⏰ Próxima semana"], buttons.Select(b => b.Label));
    }

    [Fact]
    public void A_recurring_reminder_also_offers_cancelling_the_series()
    {
        var buttons = ReminderMessage.Buttons(New("daily"));

        Assert.Equal(["✓ Esta vez", "⏰ En 1 hora", "⏰ Mañana", "⏰ Próxima semana", "✗ Cancelar serie"], buttons.Select(b => b.Label));
    }

    [Fact]
    public void Every_callback_payload_fits_telegrams_budget()
    {
        foreach (var button in ReminderMessage.Buttons(New("monthly:28")))
        {
            Assert.True(Encoding.UTF8.GetByteCount(button.Data) <= ReminderMessage.MaxCallbackDataBytes, button.Data);
        }
    }

    // ---- Callback payloads ----

    [Theory]
    [InlineData(ReminderAction.Confirm)]
    [InlineData(ReminderAction.SnoozeHour)]
    [InlineData(ReminderAction.SnoozeTomorrow)]
    [InlineData(ReminderAction.SnoozeNextWeek)]
    [InlineData(ReminderAction.CancelSeries)]
    public void A_payload_round_trips_its_action_reminder_and_occurrence(ReminderAction action)
    {
        var reminderId = Guid.NewGuid();
        var occurrence = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);

        var parsed = ReminderMessage.Parse(ReminderMessage.Data(action, reminderId, occurrence));

        Assert.NotNull(parsed);
        Assert.Equal(action, parsed.Action);
        Assert.Equal(reminderId, parsed.ReminderId);
        Assert.Equal(occurrence, parsed.OccurrenceDueAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("ok:not-a-guid:123")]
    [InlineData("zz:cb8e4bd4a7d94a0b9f1e2d3c4b5a6978:123")]
    [InlineData("ok:cb8e4bd4a7d94a0b9f1e2d3c4b5a6978")]
    [InlineData("ok:cb8e4bd4a7d94a0b9f1e2d3c4b5a6978:later")]
    public void An_unparseable_payload_is_ignored(string? data)
        => Assert.Null(ReminderMessage.Parse(data));

    // ---- Text ----

    [Fact]
    public void A_free_text_reminder_shows_only_its_text()
    {
        var text = ReminderMessage.Text(New(), null, BaseUrl);

        Assert.Equal("🔔 <b>Renovar el certificado SSL</b>", text);
    }

    [Fact]
    public void A_linked_reminder_shows_a_link_to_the_note_with_its_title()
    {
        var noteId = NoteId.Create(Guid.NewGuid()).Value;

        var text = ReminderMessage.Text(New(noteId: noteId), "Volúmenes de Docker", BaseUrl);

        Assert.Contains($"https://synap.sergioizq.com/app/notes/{noteId.Value}", text);
        Assert.Contains("Volúmenes de Docker", text);
    }

    [Fact]
    public void A_linked_reminder_whose_note_has_no_title_still_links()
    {
        var text = ReminderMessage.Text(New(noteId: NoteId.Create(Guid.NewGuid()).Value), null, BaseUrl);

        Assert.Contains("Ver la nota", text);
    }

    [Fact]
    public void A_recurring_reminder_says_how_it_repeats()
    {
        Assert.Contains("Todos los días", ReminderMessage.Text(New("daily"), null, BaseUrl));
        Assert.Contains("Todos los lunes", ReminderMessage.Text(New("weekly:0"), null, BaseUrl));
        Assert.Contains("El día 15 de cada mes", ReminderMessage.Text(New("monthly:15"), null, BaseUrl));
    }

    [Fact]
    public void The_users_own_text_cannot_inject_html_into_the_message()
    {
        var text = ReminderMessage.Text(New(text: "<b>ojo</b> & <a href=\"x\">esto</a>"), null, BaseUrl);

        Assert.DoesNotContain("<a href=\"x\">", text);
        Assert.Contains("&lt;b&gt;ojo&lt;/b&gt; &amp;", text);
    }

    // ---- Outcomes ----

    [Fact]
    public void Confirming_a_one_off_reminder_just_says_it_is_done()
        => Assert.Equal("✓ Hecho.", ReminderMessage.Outcome(New(), ReminderAction.Confirm, Madrid));

    [Fact]
    public void Confirming_a_recurring_reminder_says_when_it_comes_back()
    {
        var reminder = New("daily");
        reminder.MarkSent(Now.AddDays(1));
        reminder.Confirm(Now.AddDays(1), Madrid);

        var outcome = ReminderMessage.Outcome(reminder, ReminderAction.Confirm, Madrid);

        Assert.StartsWith("✓ Hecho. Te lo recuerdo otra vez el ", outcome);
        Assert.Contains("jueves 1 de octubre", outcome);
    }

    [Fact]
    public void Cancelling_a_series_says_it_will_not_come_back()
        => Assert.Contains("Serie cancelada", ReminderMessage.Outcome(New("daily"), ReminderAction.CancelSeries, Madrid));

    [Fact]
    public void A_snooze_says_the_new_moment()
    {
        var reminder = New();
        reminder.Snooze(new DateTime(2026, 9, 30, 7, 0, 0, DateTimeKind.Utc));

        // 07:00 UTC is 09:00 in Madrid in September.
        Assert.Contains("miércoles 30 de septiembre a las 09:00", ReminderMessage.Outcome(reminder, ReminderAction.SnoozeTomorrow, Madrid));
    }

    [Fact]
    public void A_moment_with_no_timezone_is_shown_in_utc()
        => Assert.Contains("a las 07:00", ReminderMessage.Moment(new DateTime(2026, 9, 30, 7, 0, 0, DateTimeKind.Utc), null));
}
