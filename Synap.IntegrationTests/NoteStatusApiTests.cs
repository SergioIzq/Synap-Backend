using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// note-status task 3.4 - the status filter over the real HTTP surface, specs/knowledge-vault
/// "Search and listing filtered by status". Each test registers its own user, so the shared
/// database does not interfere.
/// </summary>
[Collection(ApiCollection.Name)]
public class NoteStatusApiTests
{
    private readonly ApiFixture _api;

    public NoteStatusApiTests(ApiFixture api) => _api = api;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    /// <summary>Creates a note carrying the given status (or none) and returns its id.</summary>
    private static async Task<Guid> NoteAsync(HttpClient client, string title, string? status)
    {
        var response = await client.PostAsJsonAsync("/api/notes", new { type = "text", title, content = "contenido", status });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetGuid();
    }

    private static async Task<string[]> SearchTitlesAsync(HttpClient client, string? status = null)
    {
        var url = status is null ? "/api/notes/search" : $"/api/notes/search?status={status}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("title").GetString()!).OrderBy(t => t).ToArray();
    }

    private static async Task SeedAsync(HttpClient client)
    {
        await NoteAsync(client, "En curso", "inProgress");
        await NoteAsync(client, "Hecha", "completed");
        await NoteAsync(client, "Pausada", "paused");
        await NoteAsync(client, "Pendiente", "pending");
        await NoteAsync(client, "Sin estado", null);
    }

    /// <summary>
    /// specs/knowledge-vault "Completed notes are left out by default" and "A note without a
    /// status is never hidden by default" - the two halves of the default, over HTTP.
    /// </summary>
    [Fact]
    public async Task By_default_completed_is_left_out_and_unmarked_notes_are_shown()
    {
        var client = await SignedInAsync();
        await SeedAsync(client);

        Assert.Equal(["En curso", "Pausada", "Pendiente", "Sin estado"], await SearchTitlesAsync(client));
    }

    [Fact]
    public async Task Completed_asked_for_explicitly()
    {
        var client = await SignedInAsync();
        await SeedAsync(client);

        Assert.Equal(["Hecha"], await SearchTitlesAsync(client, "completed"));
    }

    [Fact]
    public async Task None_returns_only_the_notes_that_are_not_work()
    {
        var client = await SignedInAsync();
        await SeedAsync(client);

        Assert.Equal(["Sin estado"], await SearchTitlesAsync(client, "none"));
    }

    [Fact]
    public async Task Several_statuses_at_once()
    {
        var client = await SignedInAsync();
        await SeedAsync(client);

        Assert.Equal(["En curso", "Pendiente"], await SearchTitlesAsync(client, "pending,inProgress"));
    }

    /// <summary>specs/knowledge-vault "Everything asked for explicitly".</summary>
    [Fact]
    public async Task All_four_statuses_plus_none_returns_everything()
    {
        var client = await SignedInAsync();
        await SeedAsync(client);

        Assert.Equal(
            ["En curso", "Hecha", "Pausada", "Pendiente", "Sin estado"],
            await SearchTitlesAsync(client, "pending,inProgress,paused,completed,none"));
    }

    /// <summary>specs/knowledge-vault "An unknown value in the status filter".</summary>
    [Fact]
    public async Task An_unknown_status_is_rejected_in_spanish_naming_what_is_accepted()
    {
        var client = await SignedInAsync();

        var response = await client.GetAsync("/api/notes/search?status=archivada");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = await response.ErrorMessageAsync();
        Assert.StartsWith("El estado de la nota debe ser uno de:", message);
        Assert.Contains("inProgress", message);
        Assert.Contains("none", message);
    }

    /// <summary>specs/knowledge-vault "Status in search results".</summary>
    [Fact]
    public async Task A_notes_status_comes_back_with_the_note()
    {
        var client = await SignedInAsync();
        var marked = await NoteAsync(client, "Marcada", "inProgress");
        var unmarked = await NoteAsync(client, "Sin marcar", null);

        Assert.Equal("inProgress", await StatusOfAsync(client, marked));
        Assert.Null(await StatusOfAsync(client, unmarked));
    }

    // ---- The status endpoint (tasks 4.2 and 4.3) ----

    private static Task<HttpResponseMessage> SetStatusAsync(HttpClient client, Guid id, string? status)
        => client.PatchAsJsonAsync($"/api/notes/{id}/status", new { status });

    /// <summary>specs/knowledge-vault "Mark a note with a status" and "Clearing a status".</summary>
    [Fact]
    public async Task A_note_can_be_marked_and_cleared_through_its_own_endpoint()
    {
        var client = await SignedInAsync();
        var id = await NoteAsync(client, "Migrar auth", null);

        (await SetStatusAsync(client, id, "inProgress")).EnsureSuccessStatusCode();
        Assert.Equal("inProgress", await StatusOfAsync(client, id));

        (await SetStatusAsync(client, id, "completed")).EnsureSuccessStatusCode();
        Assert.Equal("completed", await StatusOfAsync(client, id));

        (await SetStatusAsync(client, id, null)).EnsureSuccessStatusCode();
        Assert.Null(await StatusOfAsync(client, id));
    }

    /// <summary>specs/knowledge-vault "Another user's note cannot be marked".</summary>
    [Fact]
    public async Task Another_users_note_cannot_be_marked()
    {
        var mine = await SignedInAsync();
        var theirs = await SignedInAsync();
        var theirNote = await NoteAsync(theirs, "Su nota", "pending");

        var response = await SetStatusAsync(mine, theirNote, "completed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("pending", await StatusOfAsync(theirs, theirNote));
    }

    [Fact]
    public async Task An_unknown_status_is_rejected_by_the_status_endpoint()
    {
        var client = await SignedInAsync();
        var id = await NoteAsync(client, "Migrar auth", "pending");

        var response = await SetStatusAsync(client, id, "archivada");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("pending", await StatusOfAsync(client, id));
    }

    /// <summary>
    /// note-status task 4.3 - specs/knowledge-vault "Changing a status is not editing the note",
    /// both halves, over HTTP. The second half is what protects the briefing's "untouched for N
    /// days" signal (design.md Decision 5).
    /// </summary>
    [Fact]
    public async Task A_status_change_does_not_touch_the_note_and_an_edit_does_not_touch_the_status()
    {
        var client = await SignedInAsync();
        var id = await NoteAsync(client, "Migrar auth", null);
        var before = await NoteAsync2(client, id);

        (await SetStatusAsync(client, id, "inProgress")).EnsureSuccessStatusCode();
        var afterMarking = await NoteAsync2(client, id);

        Assert.Equal(before.GetProperty("title").GetString(), afterMarking.GetProperty("title").GetString());
        Assert.Equal(before.GetProperty("content").GetString(), afterMarking.GetProperty("content").GetString());
        Assert.Equal(
            before.GetProperty("updatedAt").GetDateTime(),
            afterMarking.GetProperty("updatedAt").GetDateTime());

        var update = await client.PutAsJsonAsync($"/api/notes/{id}", new { title = "Migrar auth a OAuth", content = "otro contenido" });
        update.EnsureSuccessStatusCode();
        var afterEditing = await NoteAsync2(client, id);

        Assert.Equal("Migrar auth a OAuth", afterEditing.GetProperty("title").GetString());
        Assert.NotEqual(
            before.GetProperty("updatedAt").GetDateTime(),
            afterEditing.GetProperty("updatedAt").GetDateTime());
        Assert.Equal("inProgress", await StatusOfAsync(client, id));
    }

    // ---- Status at capture (task 5.2) ----

    /// <summary>specs/knowledge-vault "Create a note with a status".</summary>
    [Fact]
    public async Task A_note_is_created_carrying_a_status_in_one_request()
    {
        var client = await SignedInAsync();

        var id = await NoteAsync(client, "Migrar auth", "pending");

        Assert.Equal("pending", await StatusOfAsync(client, id));
    }

    /// <summary>specs/knowledge-vault "Create a note with an invalid status" - nothing is created.</summary>
    [Fact]
    public async Task An_invalid_status_at_creation_rejects_the_whole_note()
    {
        var client = await SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/notes",
            new { type = "text", title = "No debería existir", content = "c", status = "archivada", tags = new[] { "infra" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("El estado de la nota debe ser uno de:", await response.ErrorMessageAsync());
        Assert.Empty(await SearchTitlesAsync(client));
        var tags = (await (await client.GetAsync("/api/tags")).ReadJsonAsync()).GetProperty("value").EnumerateArray();
        Assert.Empty(tags);
    }

    private static async Task<JsonElement> NoteAsync2(HttpClient client, Guid id)
        => (await (await client.GetAsync($"/api/notes/{id}")).ReadJsonAsync()).GetProperty("value");

    /// <summary>
    /// Null for a note carrying no status, whether the serializer writes it as null or leaves the
    /// property out - both say the same thing on the wire, and the spec only promises that a note
    /// with no status "reports that it has none".
    /// </summary>
    internal static async Task<string?> StatusOfAsync(HttpClient client, Guid id)
    {
        var note = (await (await client.GetAsync($"/api/notes/{id}")).ReadJsonAsync()).GetProperty("value");
        return note.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null
            ? status.GetString()
            : null;
    }
}
