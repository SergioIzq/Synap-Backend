using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// smart-resurfacing 1.6 — GET /api/notes/{id}/connections:
/// nota con conexiones, nota aún sin analizar (Pending), y nota ajena (404).
/// </summary>
[Collection(ApiCollection.Name)]
public class NoteConnectionsApiTests
{
    private readonly ApiFixture _api;

    public NoteConnectionsApiTests(ApiFixture api) => _api = api;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    private static async Task<Guid> CreateNoteAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/notes", new { type = "text", title = "Helm upgrade", content = "contenido" });
        resp.EnsureSuccessStatusCode();
        return (await resp.ReadJsonAsync()).GetProperty("value").GetGuid();
    }

    [Fact]
    public async Task Returns_connections_and_common_topic_when_ai_service_responds()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        var conn1 = Guid.NewGuid();
        var conn2 = Guid.NewGuid();
        _api.Ai.SetConnections(noteId, new Domain.NoteConnectionsResult(
            Domain.NoteConnectionStatus.Ready,
            [
                new Domain.RelatedNote(conn1, "Kubernetes A", "contenido", Domain.NoteType.Text, 0.80),
                new Domain.RelatedNote(conn2, "Kubernetes B", "contenido", Domain.NoteType.Text, 0.75),
            ]));

        var resp = await client.GetAsync($"/api/notes/{noteId}/connections");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.ReadJsonAsync()).GetProperty("value");
        Assert.Equal("Ready", body.GetProperty("status").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, body.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task Returns_Pending_when_note_is_not_yet_indexed()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);

        _api.Ai.SetConnections(noteId, new Domain.NoteConnectionsResult(
            Domain.NoteConnectionStatus.Pending, []));

        var resp = await client.GetAsync($"/api/notes/{noteId}/connections");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.ReadJsonAsync()).GetProperty("value");
        Assert.Equal("Pending", body.GetProperty("status").GetString(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_503_when_ai_service_is_unavailable()
    {
        var client = await SignedInAsync();
        var noteId = await CreateNoteAsync(client);
        // No SetConnections → el fake lanza HttpRequestException

        var resp = await client.GetAsync($"/api/notes/{noteId}/connections");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_a_note_that_belongs_to_another_user()
    {
        var clientA = await SignedInAsync();
        var clientB = await SignedInAsync();
        var noteId = await CreateNoteAsync(clientA);

        var resp = await clientB.GetAsync($"/api/notes/{noteId}/connections");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
