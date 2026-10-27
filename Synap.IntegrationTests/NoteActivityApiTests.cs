using Dapper;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// smart-resurfacing 1.7 — POST /api/notes/{id}/view:
/// actividad registrada, no cuenta como edición, nota ajena.
/// También verifica que GET, búsqueda y listado no mueven last_viewed_at.
/// </summary>
[Collection(ApiCollection.Name)]
public class NoteActivityApiTests
{
    private readonly ApiFixture _api;

    public NoteActivityApiTests(ApiFixture api) => _api = api;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    private static async Task<Guid> CreateNoteAsync(HttpClient client, string title = "Helm upgrade")
    {
        var resp = await client.PostAsJsonAsync("/api/notes", new { type = "text", title, content = "contenido" });
        resp.EnsureSuccessStatusCode();
        return (await resp.ReadJsonAsync()).GetProperty("value").GetGuid();
    }

    private static async Task<JsonElement> GetNoteAsync(HttpClient client, Guid id)
        => (await (await client.GetAsync($"/api/notes/{id}")).ReadJsonAsync()).GetProperty("value");

    [Fact]
    public async Task View_sets_last_viewed_at()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        var note = await GetNoteAsync(client, noteId);
        Assert.True(note.GetProperty("lastViewedAt").ValueKind == JsonValueKind.Null);

        var resp = await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        note = await GetNoteAsync(client, noteId);
        Assert.NotEqual(JsonValueKind.Null, note.GetProperty("lastViewedAt").ValueKind);
    }

    [Fact]
    public async Task View_does_not_change_UpdatedAt()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        var before = await GetNoteAsync(client, noteId);
        var updatedAtBefore = before.GetProperty("updatedAt").GetString();

        await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });

        var after = await GetNoteAsync(client, noteId);
        Assert.Equal(updatedAtBefore, after.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task View_returns_404_for_another_users_note()
    {
        var clientA = await SignedInAsync();
        var clientB = await SignedInAsync();
        var noteId = await CreateNoteAsync(clientA);

        var resp = await clientB.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_note_does_not_move_last_viewed_at()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        // Registrar actividad explícita, luego leer repetidamente
        await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });
        var after = await GetNoteAsync(client, noteId);
        var viewedAt = after.GetProperty("lastViewedAt").GetString();

        // Leer varias veces
        await client.GetAsync($"/api/notes/{noteId}");
        await client.GetAsync($"/api/notes/{noteId}");

        var final = await GetNoteAsync(client, noteId);
        Assert.Equal(viewedAt, final.GetProperty("lastViewedAt").GetString());
    }

    [Fact]
    public async Task Search_does_not_move_last_viewed_at()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client, "Kubernetes deploy");

        await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });
        var after = await GetNoteAsync(client, noteId);
        var viewedAt = after.GetProperty("lastViewedAt").GetString();

        await client.GetAsync("/api/notes/search?q=Kubernetes");

        var final = await GetNoteAsync(client, noteId);
        Assert.Equal(viewedAt, final.GetProperty("lastViewedAt").GetString());
    }

    /// <summary>
    /// smart-resurfacing 1.12 — «Abrir la nota rescatada la despierta»: tras POST /view,
    /// la nota deja de ser candidata en el siguiente briefing porque su last_viewed_at
    /// pasa a ser reciente (posterior a DormantAfter).
    /// </summary>
    [Fact]
    public async Task Viewing_a_resurfaced_note_removes_it_from_dormant_candidates()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        // Simular que la nota lleva más de DormantAfter=30 días sin actividad
        await using var conn = new NpgsqlConnection(_api.ConnectionString);
        var old = DateTime.UtcNow.AddDays(-40);
        await conn.ExecuteAsync(
            "UPDATE notes SET updated_at = @old, last_viewed_at = NULL WHERE id = @id",
            new { old, id = noteId });

        // La nota debe ser candidata ahora (sin estado, última actividad vieja, fuera de cooldown)
        var candidateBefore = await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM notes
            WHERE id = @id
              AND status IS NULL
              AND GREATEST(updated_at, COALESCE(last_viewed_at, '-infinity'), COALESCE(last_resurfaced_at, '-infinity')) < NOW() - INTERVAL '30 days'
              AND (last_resurfaced_at IS NULL OR last_resurfaced_at < NOW() - INTERVAL '30 days')
            """,
            new { id = noteId });
        Assert.NotNull(candidateBefore);

        // Abrir la nota
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { })).EnsureSuccessStatusCode();

        // Ahora ya no debe ser candidata (last_viewed_at es reciente)
        var candidateAfter = await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM notes
            WHERE id = @id
              AND status IS NULL
              AND GREATEST(updated_at, COALESCE(last_viewed_at, '-infinity'), COALESCE(last_resurfaced_at, '-infinity')) < NOW() - INTERVAL '30 days'
            """,
            new { id = noteId });
        Assert.Null(candidateAfter);
    }

    /// <summary>
    /// smart-resurfacing 1.11 — «Aparecer no es abrir»: que el dispatcher marque last_resurfaced_at
    /// no debe mover last_viewed_at.
    /// </summary>
    [Fact]
    public async Task Resurfacing_a_note_does_not_move_last_viewed_at()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        await client.PostAsJsonAsync($"/api/notes/{noteId}/view", new { });
        var afterView = await GetNoteAsync(client, noteId);
        var viewedAt = afterView.GetProperty("lastViewedAt").GetString();

        // Simular que el briefing marca la nota como rescatada
        await using var conn = new NpgsqlConnection(_api.ConnectionString);
        await conn.ExecuteAsync(
            "UPDATE notes SET last_resurfaced_at = @now WHERE id = @id",
            new { now = DateTime.UtcNow, id = noteId });

        var afterResurface = await GetNoteAsync(client, noteId);
        Assert.Equal(viewedAt, afterResurface.GetProperty("lastViewedAt").GetString());
    }
}
