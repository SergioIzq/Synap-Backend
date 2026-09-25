using Dapper;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>branding-and-note-composer tasks 1.1/1.3 - specs/knowledge-vault "Capture a note".</summary>
[Collection(ApiCollection.Name)]
public class NoteComposerApiTests
{
    private readonly ApiFixture _api;

    public NoteComposerApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, string Email)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        return (client.WithBearer(token), email);
    }

    private static async Task<System.Text.Json.JsonElement> GetNoteAsync(HttpClient client, Guid id)
        => (await (await client.GetAsync($"/api/notes/{id}")).ReadJsonAsync()).GetProperty("value");

    [Fact]
    public async Task Note_is_created_with_title_and_new_and_existing_tags_in_one_request()
    {
        var (client, _) = await SignedInAsync();
        var first = await client.PostAsJsonAsync("/api/notes", new { type = "text", title = "Primera", content = "a", tags = new[] { "docker" } });
        first.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/notes",
            new { type = "codeSnippet", title = "  Reintentos  ", content = "retry()", tags = new[] { "#Docker", "docker", "fix", " js " } });

        response.EnsureSuccessStatusCode();
        var note = await GetNoteAsync(client, (await response.ReadJsonAsync()).GetProperty("value").GetGuid());
        Assert.Equal("Reintentos", note.GetProperty("title").GetString());
        Assert.Equal("codeSnippet", note.GetProperty("type").GetString());
        var tags = note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).OrderBy(t => t).ToList();
        // "#Docker" and "docker" collapse into one; the existing "docker" tag isn't duplicated.
        Assert.Equal(3, tags.Count);
        Assert.Contains("fix", tags);
        Assert.Contains("js", tags);

        var allTags = (await (await client.GetAsync("/api/tags")).ReadJsonAsync()).GetProperty("value").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Single(allTags, t => string.Equals(t, "docker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_type_is_inferred_a_lone_url_becomes_a_bookmark()
    {
        var (client, _) = await SignedInAsync();

        var link = await client.PostAsJsonAsync("/api/notes", new { content = "https://example.com/articulo" });
        var text = await client.PostAsJsonAsync("/api/notes", new { content = "mira https://example.com luego" });

        Assert.Equal("bookmark", (await GetNoteAsync(client, (await link.ReadJsonAsync()).GetProperty("value").GetGuid())).GetProperty("type").GetString());
        Assert.Equal("text", (await GetNoteAsync(client, (await text.ReadJsonAsync()).GetProperty("value").GetGuid())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_blank_tag_rejects_the_whole_note_and_creates_nothing()
    {
        var (client, email) = await SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/notes", new { content = "no debería existir", tags = new[] { "valida", "  " } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("El nombre de la etiqueta no puede estar vacío.", await response.ErrorMessageAsync());
        await using var connection = await _api.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE email = @email", new { email });
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM notes WHERE user_id = @userId", new { userId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM tags WHERE user_id = @userId", new { userId }));
    }

    [Fact]
    public async Task Search_returns_camelCase_types()
    {
        var (client, _) = await SignedInAsync();
        await client.PostAsJsonAsync("/api/notes", new { type = "bookmark", content = "https://example.com" });

        var body = await (await client.GetAsync("/api/notes/search")).Content.ReadAsStringAsync();

        Assert.Contains("\"type\":\"bookmark\"", body);
    }

    [Fact]
    public async Task Quick_capture_still_accepts_any_casing_for_the_type()
    {
        var (client, _) = await SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/notes/quick-capture", new { content = "x", type = "CodeSnippet" });

        response.EnsureSuccessStatusCode();
    }
}
