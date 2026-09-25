using Dapper;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>backend-hardening tasks 1.2, 1.3, 2.1-2.3 - specs/identity through the real HTTP pipeline.</summary>
[Collection(ApiCollection.Name)]
public class AccountApiTests
{
    private readonly ApiFixture _api;

    public AccountApiTests(ApiFixture api) => _api = api;

    [Fact]
    public async Task Profile_returns_only_the_signed_in_users_email_and_creation_date()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        await _api.CreateClient().RegisterAndLoginAsync(); // someone else

        var response = await client.WithBearer(token).GetAsync("/api/users/me");

        response.EnsureSuccessStatusCode();
        var value = (await response.ReadJsonAsync()).GetProperty("value");
        Assert.Equal(email, value.GetProperty("email").GetString());
        Assert.True(value.TryGetProperty("createdAt", out _));
        Assert.Equal(2, value.EnumerateObject().Count());
    }

    [Fact]
    public async Task Change_password_switches_which_password_logs_in()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();

        var response = await client.WithBearer(token).PutAsJsonAsync("/api/users/me/password",
            new { currentPassword = ApiClientExtensions.Password, newPassword = "OtraClave-2026" });

        response.EnsureSuccessStatusCode();
        var anonymous = _api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new { email, password = ApiClientExtensions.Password })).StatusCode);
        Assert.NotEmpty(await anonymous.LoginAsync(email, "OtraClave-2026"));
    }

    [Fact]
    public async Task Change_password_with_wrong_current_password_is_rejected_without_ending_the_session()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();

        var response = await client.WithBearer(token).PutAsJsonAsync("/api/users/me/password",
            new { currentPassword = "no-es-esta", newPassword = "OtraClave-2026" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("La contraseña actual no es correcta.", await response.ErrorMessageAsync());
        Assert.NotEmpty(await _api.CreateClient().LoginAsync(email, ApiClientExtensions.Password));
    }

    [Theory]
    [InlineData("corta")]
    [InlineData("")]
    public async Task Change_password_to_an_invalid_password_is_rejected(string newPassword)
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();

        var response = await client.WithBearer(token).PutAsJsonAsync("/api/users/me/password",
            new { currentPassword = ApiClientExtensions.Password, newPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("entre 8 y 128", await response.ErrorMessageAsync());
    }

    [Fact]
    public async Task Registration_applies_the_same_password_rules()
    {
        var response = await _api.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { email = $"{Guid.NewGuid():N}@example.com", password = "corta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("entre 8 y 128", await response.ErrorMessageAsync());
    }

    [Fact]
    public async Task Delete_account_with_wrong_password_deletes_nothing()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();

        var response = await client.WithBearer(token).DeleteAsJsonAsync("/api/users/me", new { password = "no-es-esta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task Delete_account_removes_all_its_data_revokes_its_tokens_and_frees_the_email()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);

        var noteResponse = await client.PostAsJsonAsync("/api/notes", new { type = "text", title = "Mía", content = "contenido" });
        var noteId = (await noteResponse.ReadJsonAsync()).GetProperty("value").GetGuid();
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/tags", new { tagName = "privada" })).EnsureSuccessStatusCode();
        var apiToken = (await (await client.PostAsync("/api/auth/api-token", null)).ReadJsonAsync()).GetProperty("value").GetString()!;

        // Another user's data must survive.
        var other = _api.CreateClient();
        var (_, otherToken) = await other.RegisterAndLoginAsync();
        var otherNote = await other.WithBearer(otherToken).PostAsJsonAsync("/api/notes", new { type = "text", title = "Ajena", content = "x" });
        var otherNoteId = (await otherNote.ReadJsonAsync()).GetProperty("value").GetGuid();

        await using (var connection = await _api.OpenConnectionAsync())
        {
            // The embedding job needs the AI service - insert one by hand to prove the cascade.
            await connection.ExecuteAsync(
                "INSERT INTO note_embeddings (note_id, user_id, embedding) VALUES (@noteId, (SELECT user_id FROM notes WHERE id = @noteId), array_fill(0.1, ARRAY[384])::vector)",
                new { noteId });
        }

        var userId = await ScalarAsync<Guid>("SELECT id FROM users WHERE email = @email", new { email });

        var response = await client.DeleteAsJsonAsync("/api/users/me", new { password = ApiClientExtensions.Password });
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM users WHERE id = @userId", new { userId }));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM notes WHERE user_id = @userId", new { userId }));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM tags WHERE user_id = @userId", new { userId }));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM note_embeddings WHERE user_id = @userId", new { userId }));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM note_tags WHERE note_id = @noteId", new { noteId }));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM notes WHERE id = @otherNoteId", new { otherNoteId }));

        // The old session JWT and the personal access token both stop working.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
        var withApiToken = _api.CreateClient().WithBearer(apiToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withApiToken.GetAsync("/api/notes/search")).StatusCode);

        // The email can be registered again (task 2.3).
        var again = await _api.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = ApiClientExtensions.Password });
        again.EnsureSuccessStatusCode();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameters)
    {
        await using var connection = await _api.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<T>(sql, parameters) ?? throw new InvalidOperationException(sql);
    }
}

/// <summary>backend-hardening tasks 4.5/4.6 - single note and tag list never cross users.</summary>
[Collection(ApiCollection.Name)]
public class NoteLookupApiTests
{
    private readonly ApiFixture _api;

    public NoteLookupApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, Guid NoteId)> UserWithTaggedNoteAsync(string tag)
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);

        var created = await client.PostAsJsonAsync("/api/notes", new { type = "text", title = "Nota", content = "contenido" });
        var noteId = (await created.ReadJsonAsync()).GetProperty("value").GetGuid();
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/tags", new { tagName = tag })).EnsureSuccessStatusCode();
        return (client, noteId);
    }

    [Fact]
    public async Task Owner_gets_the_note_with_its_tags()
    {
        var (client, noteId) = await UserWithTaggedNoteAsync("propia");

        var response = await client.GetAsync($"/api/notes/{noteId}");

        response.EnsureSuccessStatusCode();
        var value = (await response.ReadJsonAsync()).GetProperty("value");
        Assert.Equal(noteId, value.GetProperty("id").GetGuid());
        Assert.Equal("propia", value.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Another_users_note_is_indistinguishable_from_a_missing_one()
    {
        var (_, othersNoteId) = await UserWithTaggedNoteAsync("ajena");
        var (me, _) = await UserWithTaggedNoteAsync("mia");

        var others = await me.GetAsync($"/api/notes/{othersNoteId}");
        var missing = await me.GetAsync($"/api/notes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, others.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.ErrorMessageAsync(), await others.ErrorMessageAsync());
    }

    [Fact]
    public async Task Tags_are_only_the_users_own_sorted_and_still_in_use()
    {
        var (client, noteId) = await UserWithTaggedNoteAsync("zeta");
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/tags", new { tagName = "alfa" })).EnsureSuccessStatusCode();
        await UserWithTaggedNoteAsync("de-otro");

        var tags = (await (await client.GetAsync("/api/tags")).ReadJsonAsync()).GetProperty("value")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["alfa", "zeta"], tags);

        (await client.DeleteAsync($"/api/notes/{noteId}")).EnsureSuccessStatusCode();
        var afterDelete = (await (await client.GetAsync("/api/tags")).ReadJsonAsync()).GetProperty("value");
        Assert.Equal(0, afterDelete.GetArrayLength());
    }
}
