using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Notes;
using Synap.Infrastructure.Persistence.Data.Reminders;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// assistant-reminders tasks 2.4 and 2.5 - the reminders table against a real Postgres, where the
/// filtered index, the two cascade rules and the recurrence converter actually apply.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReminderPersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public ReminderPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    private static User NewUser()
        => User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));

    [Fact]
    public async Task A_reminder_round_trips_with_its_recurrence_and_note_link()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        var note = Note.Create(user.Id, NoteType.Text, "Certificados", "renovar el SSL");
        context.Add(note);
        await context.SaveChangesAsync();

        var reminder = Reminder.Create(user.Id, "Renovar el certificado SSL", Now.AddDays(1), Now, note.Id, "weekly:2").Value;
        var reminders = new ReminderWriteRepository(context);
        await reminders.CreateAsync(reminder, default);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await reminders.GetOwnedByUserAsync(reminder.Id.Value, user.Id.Value);

        Assert.NotNull(stored);
        Assert.Equal("Renovar el certificado SSL", stored.Text);
        Assert.Equal(Now.AddDays(1), stored.DueAt);
        Assert.Equal(note.Id, stored.NoteId);
        Assert.Equal("weekly:2", stored.Recurrence?.ToString());
        Assert.True(stored.IsPending);
    }

    [Fact]
    public async Task A_reminder_of_another_user_is_not_found()
    {
        await using var context = _fixture.CreateContext();
        var owner = NewUser();
        var other = NewUser();
        context.AddRange(owner, other);
        await context.SaveChangesAsync();

        var reminders = new ReminderWriteRepository(context);
        var reminder = Reminder.Create(owner.Id, "Privado", Now.AddDays(1), Now).Value;
        await reminders.CreateAsync(reminder, default);
        await context.SaveChangesAsync();

        Assert.Null(await reminders.GetOwnedByUserAsync(reminder.Id.Value, other.Id.Value));
    }

    [Fact]
    public async Task ListDueAsync_returns_only_undelivered_reminders_that_are_already_due()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        user.SetTimezone("Europe/Madrid");
        user.CompleteTelegramLink("987654321");
        context.Add(user);
        await context.SaveChangesAsync();

        var reminders = new ReminderWriteRepository(context);
        var due = Reminder.Create(user.Id, "Vencido", Now.AddMinutes(1), Now).Value;
        var alsoDue = Reminder.Create(user.Id, "También vencido", Now.AddMinutes(2), Now).Value;
        var future = Reminder.Create(user.Id, "Futuro", Now.AddDays(30), Now).Value;
        var alreadySent = Reminder.Create(user.Id, "Ya enviado", Now.AddMinutes(3), Now).Value;
        alreadySent.MarkSent(Now.AddMinutes(3));
        var cancelled = Reminder.Create(user.Id, "Serie cancelada", Now.AddMinutes(4), Now, recurrence: "daily").Value;
        cancelled.CancelSeries();

        foreach (var reminder in new[] { due, alsoDue, future, alreadySent, cancelled })
        {
            await reminders.CreateAsync(reminder, default);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var found = await reminders.ListDueAsync(Now.AddMinutes(10), 50);

        var texts = found.Where(d => d.Reminder.UserId == user.Id).Select(d => d.Reminder.Text).ToList();
        Assert.Equal(["Vencido", "También vencido"], texts);
        Assert.All(found.Where(d => d.Reminder.UserId == user.Id), d =>
        {
            Assert.Equal("987654321", d.TelegramChatId);
            Assert.Equal("Europe/Madrid", d.Timezone);
        });
    }

    [Fact]
    public async Task ListDueAsync_carries_the_linked_notes_title()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        var note = Note.Create(user.Id, NoteType.Text, "Volúmenes de Docker", "contenido");
        context.Add(note);
        await context.SaveChangesAsync();

        var reminders = new ReminderWriteRepository(context);
        var reminder = Reminder.Create(user.Id, "Revisar esto", Now.AddMinutes(1), Now, note.Id).Value;
        await reminders.CreateAsync(reminder, default);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var found = await reminders.ListDueAsync(Now.AddMinutes(10), 50);

        var mine = Assert.Single(found, d => d.Reminder.Id == reminder.Id);
        Assert.Equal("Volúmenes de Docker", mine.NoteTitle);
    }

    [Fact]
    public async Task Pending_reminders_are_listed_soonest_first_for_their_owner_only()
    {
        await using var context = _fixture.CreateContext();
        var owner = NewUser();
        var other = NewUser();
        context.AddRange(owner, other);
        await context.SaveChangesAsync();

        var writes = new ReminderWriteRepository(context);
        await writes.CreateAsync(Reminder.Create(owner.Id, "Más tarde", Now.AddDays(3), Now).Value, default);
        await writes.CreateAsync(Reminder.Create(owner.Id, "Antes", Now.AddDays(1), Now).Value, default);
        await writes.CreateAsync(Reminder.Create(other.Id, "De otra persona", Now.AddHours(1), Now).Value, default);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var listed = await new ReminderReadRepository(context).ListPendingByUserAsync(owner.Id.Value);

        Assert.Equal(["Antes", "Más tarde"], listed.Select(r => r.Text));
    }

    [Fact]
    public async Task A_notes_reminders_are_listed_for_its_owner_and_never_for_anyone_else()
    {
        await using var context = _fixture.CreateContext();
        var owner = NewUser();
        var other = NewUser();
        context.AddRange(owner, other);
        var note = Note.Create(owner.Id, NoteType.Text, "Con recordatorios", "contenido");
        context.Add(note);
        await context.SaveChangesAsync();

        var writes = new ReminderWriteRepository(context);
        await writes.CreateAsync(Reminder.Create(owner.Id, "Primero", Now.AddDays(1), Now, note.Id).Value, default);
        await writes.CreateAsync(Reminder.Create(owner.Id, "Segundo", Now.AddDays(2), Now, note.Id).Value, default);
        await writes.CreateAsync(Reminder.Create(owner.Id, "Sin nota", Now.AddDays(1), Now).Value, default);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var reads = new ReminderReadRepository(context);

        // A note can hold more than one reminder (specs/reminders).
        var mine = await reads.ListByNoteAsync(owner.Id.Value, note.Id.Value);
        Assert.Equal(["Primero", "Segundo"], mine.Select(r => r.Text));
        Assert.All(mine, r => Assert.Equal("Con recordatorios", r.NoteTitle));

        Assert.Empty(await reads.ListByNoteAsync(other.Id.Value, note.Id.Value));
    }

    [Fact]
    public async Task Deleting_a_note_unlinks_its_reminders_and_leaves_them_pending()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        var note = Note.Create(user.Id, NoteType.Text, "A borrar", "contenido");
        context.Add(note);
        await context.SaveChangesAsync();

        var reminders = new ReminderWriteRepository(context);
        var reminder = Reminder.Create(user.Id, "Sigue vivo", Now.AddDays(1), Now, note.Id).Value;
        await reminders.CreateAsync(reminder, default);
        await context.SaveChangesAsync();

        var notes = new NoteWriteRepository(context);
        notes.Delete((await notes.GetOwnedByUserAsync(note.Id.Value, user.Id.Value))!);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await reminders.GetOwnedByUserAsync(reminder.Id.Value, user.Id.Value);

        Assert.NotNull(stored);
        Assert.Null(stored.NoteId);
        Assert.True(stored.IsPending);
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_reminders()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        var note = Note.Create(user.Id, NoteType.Text, "Con recordatorio", "contenido");
        context.Add(note);
        await context.SaveChangesAsync();

        var reminders = new ReminderWriteRepository(context);
        var linked = Reminder.Create(user.Id, "Vinculado", Now.AddDays(1), Now, note.Id).Value;
        var free = Reminder.Create(user.Id, "Suelto", Now.AddDays(1), Now).Value;
        await reminders.CreateAsync(linked, default);
        await reminders.CreateAsync(free, default);
        await context.SaveChangesAsync();

        await new UserWriteRepository(context).DeleteWithAllDataAsync(user.Id.Value);

        Assert.Null(await reminders.GetOwnedByUserAsync(linked.Id.Value, user.Id.Value));
        Assert.Null(await reminders.GetOwnedByUserAsync(free.Id.Value, user.Id.Value));
    }
}
