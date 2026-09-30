using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Briefing;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// daily-briefing tasks 2.1 to 2.3 - the briefing's three queries against a real Postgres, which
/// is the only place the tag join, the Spanish search vector and its stopwords actually behave.
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

    // ---- 2.3 Open threads ----

    [Fact]
    public async Task Notes_carrying_an_open_thread_marker_are_reported_and_others_are_not()
    {
        await using var context = _fixture.CreateContext();
        var mine = NewUser();
        var theirs = NewUser();
        context.Add(mine);
        context.Add(theirs);

        var pending = Note.Create(mine.Id, NoteType.Text, "Migración", "esto está pendiente de cerrar");
        // Lower-case "todo" is the ordinary Spanish word, not the marker: this note is closed.
        var closed = Note.Create(mine.Id, NoteType.Text, "Terminado", "todo salió bien y quedó cerrado");
        var hers = Note.Create(theirs.Id, NoteType.Text, "Suya", "pendiente de revisar");
        foreach (var note in new[] { pending, closed, hers })
        {
            context.Add(note);
        }

        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(
            mine.Id.Value, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection);

        Assert.Equal(["Migración"], section.Items.Select(n => n.Title));
    }

    /// <summary>The stemming is the point of going through the search vector at all.</summary>
    [Fact]
    public async Task A_marker_is_matched_in_its_other_forms()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "Plural", "quedan dos cosas pendientes"));
        context.Add(Note.Create(user.Id, NoteType.Text, "Gerundio", "lo estoy revisando"));
        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(
            user.Id.Value, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection);

        Assert.Equal(2, section.Total);
    }

    /// <summary>
    /// design.md Decision 4: "todo" is a Spanish stopword, so the search vector never carries it.
    /// This is the case that would silently disappear if the section relied on the index alone.
    /// </summary>
    [Fact]
    public async Task A_marker_the_search_vector_discards_is_still_found()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "Lista", "TODO: llamar al banco"));
        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(
            user.Id.Value, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection);

        Assert.Equal("Lista", Assert.Single(section.Items).Title);
    }

    /// <summary>
    /// The reason the literal match is case sensitive. "todo" is one of the commonest words in
    /// Spanish; matching it case-insensitively would put most of a vault in this section.
    /// </summary>
    [Theory]
    [InlineData("todo salió bien")]
    [InlineData("sobre todo me gustó")]
    [InlineData("estuvo todo el día trabajando")]
    public async Task The_ordinary_spanish_word_todo_is_not_a_marker(string content)
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "Cerrada", content));
        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(
            user.Id.Value, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection);

        Assert.True(section.IsEmpty, $"\"{content}\" must not read as an open thread");
    }

    [Fact]
    public async Task A_marker_in_the_title_counts_too()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "TODO del sprint", "contenido sin marcadores"));
        context.Add(Note.Create(user.Id, NoteType.Text, "Pendiente de firma", "contenido sin marcadores"));
        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(
            user.Id.Value, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection);

        Assert.Equal(2, section.Total);
    }

    [Fact]
    public async Task With_no_markers_nothing_is_reported_and_no_query_runs()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(Note.Create(user.Id, NoteType.Text, "Cualquiera", "pendiente"));
        await context.SaveChangesAsync();

        var section = await Repository().ListOpenThreadNotesAsync(user.Id.Value, [], [], BriefingLimits.ItemsPerSection);

        Assert.True(section.IsEmpty);
    }
}
