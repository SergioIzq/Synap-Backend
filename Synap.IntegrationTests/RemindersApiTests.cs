using Dapper;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// assistant-reminders tasks 8.1 to 8.3 - specs/reminders through the real HTTP pipeline, including
/// the per-user isolation rules in the style of NoteIsolationTests.
/// </summary>
[Collection(ApiCollection.Name)]
public class RemindersApiTests
{
    private readonly ApiFixture _api;

    public RemindersApiTests(ApiFixture api) => _api = api;

    private static DateTime Future => DateTime.UtcNow.AddDays(1);

    private async Task<HttpClient> SignedInAsync() => (await SignedInWithEmailAsync()).Client;

    private async Task<(HttpClient Client, string Email)> SignedInWithEmailAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        return (client.WithBearer(token), email);
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient client, string? text, DateTime dueAtUtc, Guid? noteId = null, string? recurrence = null, string? timezone = null)
        => await client.PostAsJsonAsync("/api/reminders", new { text, dueAtUtc, noteId, recurrence, timezone });

    private static async Task<Guid> CreatedAsync(
        HttpClient client, string text, DateTime dueAtUtc, Guid? noteId = null, string? recurrence = null)
    {
        var response = await CreateAsync(client, text, dueAtUtc, noteId, recurrence);
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
        => (await (await client.GetAsync("/api/reminders")).ReadJsonAsync()).GetProperty("value");

    private static async Task<Guid> CreateNoteAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/notes", new { type = "Text", title, content = "contenido" });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetGuid();
    }

    // ---- The verbs (task 8.1) ----

    [Fact]
    public async Task Reminders_are_created_listed_soonest_first_edited_and_cancelled()
    {
        var client = await SignedInAsync();
        var later = await CreatedAsync(client, "Más tarde", Future.AddDays(3));
        var sooner = await CreatedAsync(client, "Antes", Future.AddHours(1));

        var list = await ListAsync(client);
        Assert.Equal(500, list.GetProperty("maxTextLength").GetInt32());
        Assert.Equal(
            [sooner, later],
            list.GetProperty("reminders").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));

        var newMoment = Future.AddDays(5);
        var edit = await client.PutAsJsonAsync($"/api/reminders/{later}", new { text = "Renombrado", dueAtUtc = newMoment, recurrence = "daily" });
        edit.EnsureSuccessStatusCode();

        var edited = (await ListAsync(client)).GetProperty("reminders").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == later);
        Assert.Equal("Renombrado", edited.GetProperty("text").GetString());
        Assert.Equal("daily", edited.GetProperty("recurrence").GetString());

        (await client.DeleteAsync($"/api/reminders/{sooner}")).EnsureSuccessStatusCode();
        Assert.Single((await ListAsync(client)).GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task A_reminder_can_be_created_on_a_note_and_a_note_can_hold_several()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client, "Volúmenes de Docker");

        var first = await CreatedAsync(client, "Primero", Future.AddHours(1), noteId);
        var second = await CreatedAsync(client, "Segundo", Future.AddHours(2), noteId);

        var forNote = (await (await client.GetAsync($"/api/reminders/note/{noteId}")).ReadJsonAsync()).GetProperty("value");
        Assert.Equal([first, second], forNote.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.All(forNote.EnumerateArray(), r => Assert.Equal("Volúmenes de Docker", r.GetProperty("noteTitle").GetString()));
    }

    [Fact]
    public async Task Invalid_reminders_are_rejected_in_spanish()
    {
        var client = await SignedInAsync();

        var empty = await CreateAsync(client, "   ", Future);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("El recordatorio no puede estar vacío.", await empty.ErrorMessageAsync());

        var tooLong = await CreateAsync(client, new string('a', 501), Future);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("El recordatorio no puede superar los 500 caracteres.", await tooLong.ErrorMessageAsync());

        var past = await CreateAsync(client, "Tarde", DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal("El momento del recordatorio tiene que estar en el futuro.", await past.ErrorMessageAsync());

        var badRecurrence = await CreateAsync(client, "Revisar", Future, recurrence: "el tercer martes");
        Assert.Equal(HttpStatusCode.BadRequest, badRecurrence.StatusCode);
        Assert.Contains("daily", await badRecurrence.ErrorMessageAsync());

        Assert.Empty((await ListAsync(client)).GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task An_edit_to_a_past_moment_is_rejected_and_keeps_the_reminder()
    {
        var client = await SignedInAsync();
        var moment = Future;
        var id = await CreatedAsync(client, "Intacto", moment);

        var edit = await client.PutAsJsonAsync($"/api/reminders/{id}", new { text = "Otro", dueAtUtc = DateTime.UtcNow.AddDays(-1) });

        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        var unchanged = (await ListAsync(client)).GetProperty("reminders").EnumerateArray().Single();
        Assert.Equal("Intacto", unchanged.GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_unknown_reminder_is_a_404_on_edit_and_cancel()
    {
        var client = await SignedInAsync();
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/reminders/{unknown}", new { text = "x", dueAtUtc = Future })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/reminders/{unknown}")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_reminder_stores_the_browsers_timezone()
    {
        var (client, email) = await SignedInWithEmailAsync();

        await CreateAsync(client, "Con huso horario", Future, timezone: "America/New_York");

        await using var connection = await _api.OpenConnectionAsync();
        var stored = await connection.ExecuteScalarAsync<string?>(
            "SELECT timezone FROM users WHERE email = @email", new { email });
        Assert.Equal("America/New_York", stored);
    }

    [Fact]
    public async Task An_unknown_timezone_does_not_fail_the_creation()
    {
        var client = await SignedInAsync();

        var response = await CreateAsync(client, "Huso raro", Future, timezone: "Mars/Olympus_Mons");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reminders_need_authentication()
    {
        var anonymous = _api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateAsync(anonymous, "x", Future)).StatusCode);
    }

    // ---- Isolation (task 8.3) ----

    [Fact]
    public async Task One_user_never_sees_or_touches_anothers_reminders()
    {
        var owner = await SignedInAsync();
        var other = await SignedInAsync();
        var id = await CreatedAsync(owner, "Privado", Future);

        // Not listed.
        Assert.Empty((await ListAsync(other)).GetProperty("reminders").EnumerateArray());

        // Not editable, not cancellable - 404, exactly like a reminder that does not exist.
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PutAsJsonAsync($"/api/reminders/{id}", new { text = "secuestrado", dueAtUtc = Future })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/reminders/{id}")).StatusCode);

        // And still intact for its owner.
        var mine = (await ListAsync(owner)).GetProperty("reminders").EnumerateArray().Single();
        Assert.Equal("Privado", mine.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_reminder_cannot_be_linked_to_another_users_note()
    {
        var owner = await SignedInAsync();
        var other = await SignedInAsync();
        var theirNote = await CreateNoteAsync(owner, "Su nota");

        var response = await CreateAsync(other, "Espiando", Future, theirNote);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Nota no encontrada.", await response.ErrorMessageAsync());
        Assert.Empty((await ListAsync(other)).GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task Another_users_notes_reminders_are_not_listed()
    {
        var owner = await SignedInAsync();
        var other = await SignedInAsync();
        var noteId = await CreateNoteAsync(owner, "Con recordatorio");
        await CreatedAsync(owner, "Privado", Future, noteId);

        var forNote = (await (await other.GetAsync($"/api/reminders/note/{noteId}")).ReadJsonAsync()).GetProperty("value");

        Assert.Empty(forNote.EnumerateArray());
    }
}
