using Dapper;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>assistant-agent-foundations task 5.2 - specs/assistant-memory through the real HTTP pipeline.</summary>
[Collection(ApiCollection.Name)]
public class MemoryApiTests
{
    private readonly ApiFixture _api;

    public MemoryApiTests(ApiFixture api) => _api = api;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    private static async Task<Guid> AddAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/memory", new { text });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
        => (await (await client.GetAsync("/api/memory")).ReadJsonAsync()).GetProperty("value");

    [Fact]
    public async Task Entries_are_added_edited_listed_newest_first_and_deleted()
    {
        var client = await SignedInAsync();
        var first = await AddAsync(client, "Trabajo con .NET y Angular");
        var second = await AddAsync(client, "prefiero respuestas cortas");

        (await client.PutAsJsonAsync($"/api/memory/{first}", new { text = "Trabajo con .NET, Angular y Python" })).EnsureSuccessStatusCode();

        var list = await ListAsync(client);
        Assert.Equal(25, list.GetProperty("maxEntries").GetInt32());
        Assert.Equal(200, list.GetProperty("maxTextLength").GetInt32());
        var texts = list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Trabajo con .NET, Angular y Python", "prefiero respuestas cortas"], texts);

        (await client.DeleteAsync($"/api/memory/{second}")).EnsureSuccessStatusCode();
        Assert.Equal(1, (await ListAsync(client)).GetProperty("entries").GetArrayLength());

        (await client.DeleteAsync("/api/memory")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await ListAsync(client)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_and_excess_entries_are_rejected_in_spanish()
    {
        var client = await SignedInAsync();

        var tooLong = await client.PostAsJsonAsync("/api/memory", new { text = new string('a', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("El recuerdo no puede superar los 200 caracteres.", await tooLong.ErrorMessageAsync());

        for (var i = 0; i < 25; i++)
        {
            await AddAsync(client, $"hecho {i}");
        }

        var full = await client.PostAsJsonAsync("/api/memory", new { text = "uno más" });
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.StartsWith("La memoria está llena", await full.ErrorMessageAsync());
        Assert.Equal(25, (await ListAsync(client)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Another_users_entry_answers_404_and_is_unchanged()
    {
        var owner = await SignedInAsync();
        var entry = await AddAsync(owner, "privado");
        var intruder = await SignedInAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PutAsJsonAsync($"/api/memory/{entry}", new { text = "hackeado" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.DeleteAsync($"/api/memory/{entry}")).StatusCode);
        (await intruder.DeleteAsync("/api/memory")).EnsureSuccessStatusCode();

        Assert.Equal(0, (await ListAsync(intruder)).GetProperty("entries").GetArrayLength());
        var mine = (await ListAsync(owner)).GetProperty("entries");
        Assert.Equal("privado", Assert.Single(mine.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_memory()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        await AddAsync(client, "me voy");
        var survivor = await SignedInAsync();
        await AddAsync(survivor, "me quedo");

        await using var connection = await _api.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE email = @email", new { email });

        (await client.DeleteAsJsonAsync("/api/users/me", new { password = ApiClientExtensions.Password })).EnsureSuccessStatusCode();

        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM memory_entries WHERE user_id = @userId", new { userId }));
        Assert.Equal(1, (await ListAsync(survivor)).GetProperty("entries").GetArrayLength());
    }
}
