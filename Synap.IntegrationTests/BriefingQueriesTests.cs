using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Briefing;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// daily-briefing tasks 2.1 to 2.3, revised by note-status task 6.2 - the briefing's four queries
/// against a real Postgres, which is the only place the tag join and the status filters actually
/// behave.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BriefingQueriesTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public BriefingQueriesTests(PostgresFixture fixture) => _fixture = fixture;

    private BriefingReadRepository Repository() => new(new TestDbConnectionFactory(_fixture.ConnectionString));

    private static User NewUser()
        => User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));

    // ---- 2.1 Today's reminders ----

    [Fact]
    public async Task Only_the_users_pending_reminders_inside_the_day_are_reported()
    {
        await using var context = _fixture.CreateContext();
        var mine = NewUser();
        var theirs = NewUser();
        context.Add(mine);
        context.Add(theirs);

        var today = Reminder.Create(mine.Id, "hoy", Now.AddHours(6), Now).Value;
        var tomorrow = Reminder.Create(mine.Id, "mañana", Now.AddHours(30), Now).Value;
        var yesterday = Reminder.Create(mine.Id, "ayer", Now.AddHours(-30), Now.AddDays(-2)).Value;
        var sent = Reminder.Create(mine.Id, "ya enviado", Now.AddHours(7), Now).Value;
        sent.MarkSent(Now);
        var hers = Reminder.Create(theirs.Id, "suyo", Now.AddHours(6), Now).Value;
        foreach (var reminder in new[] { today, tomorrow, yesterday, sent, hers })
        {
            context.Add(reminder);
        }

        await context.SaveChangesAsync();

        var section = await Repository().ListRemindersDueAsync(
            mine.Id.Value, Now.Date, Now.Date.AddDays(1), BriefingLimits.ItemsPerSection);

        Assert.Equal(["hoy"], section.Items.Select(r => r.Text));
        Assert.Equal(1, section.Total);
    }

    [Fact]
    public async Task A_reminder_reports_the_title_of_the_note_it_is_about()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        var note = Note.Create(user.Id, NoteType.Text, "Certificados", "renovar el SSL");
        context.Add(note);
        context.Add(Reminder.Create(user.Id, "Renovar", Now.AddHours(2), Now, note.Id).Value);
        await context.SaveChangesAsync();

        var section = await Repository().ListRemindersDueAsync(
            user.Id.Value, Now.Date, Now.Date.AddDays(1), BriefingLimits.ItemsPerSection);

        Assert.Equal("Certificados", Assert.Single(section.Items).NoteTitle);
    }

    // ---- 2.2 Untagged notes ----

    [Fact]
    public async Task Only_the_users_recent_untagged_notes_are_reported()
    {
        await using var context = _fixture.CreateContext();
        var mine = NewUser();
        var theirs = NewUser();
        context.Add(mine);
        context.Add(theirs);

        var untagged = Note.Create(mine.Id, NoteType.Text, "Sin etiquetar", "algo");
        var tagged = Note.Create(mine.Id, NoteType.Text, "Etiquetada", "algo");
        var tag = Tag.Create(mine.Id, "infra");
        context.Add(tag);
        tagged.AddTag(tag);
        var hers = Note.Create(theirs.Id, NoteType.Text, "Suya", "algo");
        foreach (var note in new[] { untagged, tagged, hers })
        {
            context.Add(note);
        }

        await context.SaveChangesAsync();

        var section = await Repository().ListUntaggedNotesAsync(
            mine.Id.Value, Now.Subtract(BriefingLimits.UntaggedWindow), BriefingLimits.ItemsPerSection);

        Assert.Equal(["Sin etiquetar"], section.Items.Select(n => n.Title));
        Assert.Equal(1, section.Total);
    }

    [Fact]
    public async Task A_capped_section_still_reports_how_many_there_are()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        for (var i = 0; i < BriefingLimits.ItemsPerSection + 3; i++)
        {
            context.Add(Note.Create(user.Id, NoteType.Text, $"Nota {i}", "algo"));
        }

        await context.SaveChangesAsync();

        var section = await Repository().ListUntaggedNotesAsync(
            user.Id.Value, Now.Subtract(BriefingLimits.UntaggedWindow), BriefingLimits.ItemsPerSection);

        Assert.Equal(BriefingLimits.ItemsPerSection, section.Items.Count);
        Assert.Equal(BriefingLimits.ItemsPerSection + 3, section.Total);
        Assert.True(section.IsTruncated);
    }

    // ---- Status sections (note-status task 6.2) ----

    /// <summary>
    /// The query that replaced the marker heuristic. A note whose *text* says "pendiente" or
    /// carries "TODO" but which the user never marked is not work: that inference is gone
    /// (note-status design.md Decision 7).
    /// </summary>
    [Fact]
    public async Task Only_notes_the_user_marked_in_progress_are_reported_as_in_progress()
    {
        await using var context = _fixture.CreateContext();
        var mine = NewUser();
        var theirs = NewUser();
        context.Add(mine);
        context.Add(theirs);

        context.Add(Note.Create(mine.Id, NoteType.Text, "En curso", "contenido", NoteStatus.InProgress));
        context.Add(Note.Create(mine.Id, NoteType.Text, "Pendiente", "contenido", NoteStatus.Pending));
        context.Add(Note.Create(mine.Id, NoteType.Text, "Hecha", "contenido", NoteStatus.Completed));
        context.Add(Note.Create(mine.Id, NoteType.Text, "Sin marcar", "esto está pendiente, TODO: cerrarlo"));
        context.Add(Note.Create(theirs.Id, NoteType.Text, "Suya", "contenido", NoteStatus.InProgress));
        await context.SaveChangesAsync();

        var section = await Repository().ListInProgressNotesAsync(mine.Id.Value, BriefingLimits.ItemsPerSection);

        Assert.Equal(["En curso"], section.Items.Select(n => n.Title));
        Assert.Equal(1, section.Total);
    }

    /// <summary>
    /// The case the old heuristic got wrong in the direction nobody noticed: a note full of
    /// markers that the user never marked says nothing about whether it is open.
    /// </summary>
    [Fact]
    public async Task An_unmarked_note_whose_text_carries_the_old_markers_is_reported_in_neither_section()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "TODO del sprint", "esto está pendiente de revisar"));
        await context.SaveChangesAsync();

        var repository = Repository();

        Assert.True((await repository.ListInProgressNotesAsync(user.Id.Value, BriefingLimits.ItemsPerSection)).IsEmpty);
        Assert.True((await repository.ListPendingNotesAsync(
            user.Id.Value, Now - BriefingLimits.PausedResurfaceAfter, Now, BriefingLimits.ItemsPerSection)).IsEmpty);
    }

    [Fact]
    public async Task Only_the_users_pending_notes_are_reported_as_pending()
    {
        await using var context = _fixture.CreateContext();
        var mine = NewUser();
        var theirs = NewUser();
        context.Add(mine);
        context.Add(theirs);

        context.Add(Note.Create(mine.Id, NoteType.Text, "Pendiente", "contenido", NoteStatus.Pending));
        context.Add(Note.Create(mine.Id, NoteType.Text, "En curso", "contenido", NoteStatus.InProgress));
        context.Add(Note.Create(mine.Id, NoteType.Text, "Hecha", "contenido", NoteStatus.Completed));
        context.Add(Note.Create(theirs.Id, NoteType.Text, "Suya", "contenido", NoteStatus.Pending));
        await context.SaveChangesAsync();

        var section = await Repository().ListPendingNotesAsync(
            mine.Id.Value, Now - BriefingLimits.PausedResurfaceAfter, Now, BriefingLimits.ItemsPerSection);

        Assert.Equal(["Pendiente"], section.Items.Select(n => n.Title));
        Assert.Null(Assert.Single(section.Items).PausedForDays);
    }

    /// <summary>specs/briefing "A recently paused note stays quiet" - that is what pausing is for.</summary>
    [Fact]
    public async Task A_recently_paused_note_is_not_reported()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);

        var paused = Note.Create(user.Id, NoteType.Text, "Pausada ayer", "contenido");
        paused.SetStatus(NoteStatus.Paused);
        context.Add(paused);
        await context.SaveChangesAsync();

        // SetStatus stamps DateTime.UtcNow, so the cutoff is taken from the real clock here.
        var nowUtc = DateTime.UtcNow;
        var section = await Repository().ListPendingNotesAsync(
            user.Id.Value, nowUtc - BriefingLimits.PausedResurfaceAfter, nowUtc, BriefingLimits.ItemsPerSection);

        Assert.True(section.IsEmpty);
    }

    /// <summary>specs/briefing "A long-paused note resurfaces" - design.md Decision 6.</summary>
    [Fact]
    public async Task A_note_paused_for_longer_than_the_window_rejoins_the_pending_section()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);

        var paused = Note.Create(user.Id, NoteType.Text, "Pausada hace mucho", "contenido");
        paused.SetStatus(NoteStatus.Paused);
        context.Add(paused);
        await context.SaveChangesAsync();

        // The cutoff is moved instead of the row: the age the query reports is what matters.
        var nowUtc = DateTime.UtcNow.AddDays(34);
        var section = await Repository().ListPendingNotesAsync(
            user.Id.Value, nowUtc - BriefingLimits.PausedResurfaceAfter, nowUtc, BriefingLimits.ItemsPerSection);

        var note = Assert.Single(section.Items);
        Assert.Equal("Pausada hace mucho", note.Title);
        Assert.Equal(34, note.PausedForDays);
    }

    /// <summary>A completed note is done, and the briefing never mentions it.</summary>
    [Fact]
    public async Task A_completed_note_is_reported_in_neither_section()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "Hecha", "contenido", NoteStatus.Completed));
        await context.SaveChangesAsync();

        var repository = Repository();

        Assert.True((await repository.ListInProgressNotesAsync(user.Id.Value, BriefingLimits.ItemsPerSection)).IsEmpty);
        Assert.True((await repository.ListPendingNotesAsync(
            user.Id.Value, Now - BriefingLimits.PausedResurfaceAfter, Now, BriefingLimits.ItemsPerSection)).IsEmpty);
    }

    /// <summary>A capped section still says how many it stands for (specs/briefing).</summary>
    [Fact]
    public async Task A_capped_status_section_still_reports_its_total()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        for (var i = 0; i < BriefingLimits.ItemsPerSection + 3; i++)
        {
            context.Add(Note.Create(user.Id, NoteType.Text, $"Pendiente {i}", "contenido", NoteStatus.Pending));
        }

        await context.SaveChangesAsync();

        var section = await Repository().ListPendingNotesAsync(
            user.Id.Value, Now - BriefingLimits.PausedResurfaceAfter, Now, BriefingLimits.ItemsPerSection);

        Assert.Equal(BriefingLimits.ItemsPerSection, section.Items.Count);
        Assert.Equal(BriefingLimits.ItemsPerSection + 3, section.Total);
        Assert.True(section.IsTruncated);
    }
}
