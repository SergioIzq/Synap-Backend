using Dapper;
using Synap.Domain;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// assistant-agent-foundations tasks 6.3/6.4 - the global conversation's agent through the real
/// HTTP pipeline, DI and Postgres, with a scripted AI service: its actions are real user actions,
/// and nothing crosses users - notes, searches or memory.
/// </summary>
[Collection(ApiCollection.Name)]
public class AssistantAgentApiTests
{
    private readonly ApiFixture _api;

    public AssistantAgentApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, string Key, Guid UserId)> UserWithKeyAsync()
    {
        var key = $"gsk_agent_{Guid.NewGuid():N}";
        _api.Ai.AcceptKey(key, new LlmModel("capable/model", true));
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        (await client.PutAsJsonAsync("/api/settings/ai/groq-key", new { apiKey = key })).EnsureSuccessStatusCode();

        await using var connection = await _api.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE email = @email", new { email });
        return (client, key, userId);
    }

    private static async Task<Guid> CreateNoteAsync(HttpClient client, string title, string content)
    {
        var response = await client.PostAsJsonAsync("/api/notes", new { type = "text", title, content });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetGuid();
    }

    private static async Task<JsonElement> AskAsync(HttpClient client, string question, object[]? history = null)
    {
        var response = await client.PostAsJsonAsync("/api/assistant/ask", new { question, history });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value");
    }

    private static AgentStepResult Calls(params (string Name, object Arguments)[] calls)
        => new(AgentStepStatus.Ok, null, calls.Select((c, i) => new AgentToolCall($"c{i}", c.Name, JsonSerializer.SerializeToElement(c.Arguments))).ToList());

    private static AgentStepResult Text(string text) => new(AgentStepStatus.Ok, text, []);

    private static async Task<IReadOnlyList<string>> TagsOfAsync(HttpClient owner, Guid noteId)
        => (await (await owner.GetAsync($"/api/notes/{noteId}")).ReadJsonAsync()).GetProperty("value").GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()!).ToList();

    [Fact]
    public async Task Creates_a_note_and_remembers_as_the_user_and_reports_both()
    {
        var (client, key, _) = await UserWithKeyAsync();
        _api.Ai.ScriptSteps(key,
            Calls(("create_note", new { title = "Renovar SSL", content = "El martes renovar el certificado", tags = new[] { "infra" } }),
                  ("remember", new { text = "prefiero respuestas cortas" })),
            Text("Apuntado y recordado."));

        var answer = await AskAsync(client, "Apúntame lo del SSL con #infra y recuerda que prefiero respuestas cortas");

        Assert.Equal("ok", answer.GetProperty("status").GetString());
        var actions = answer.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(["noteCreated", "memorySaved"], actions.Select(a => a.GetProperty("type").GetString()));

        var noteId = actions[0].GetProperty("noteId").GetGuid();
        Assert.Equal(["infra"], await TagsOfAsync(client, noteId));
        var memory = (await (await client.GetAsync("/api/memory")).ReadJsonAsync()).GetProperty("value").GetProperty("entries");
        Assert.Equal("prefiero respuestas cortas", Assert.Single(memory.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Another_users_note_can_be_neither_read_nor_tagged()
    {
        var (owner, _, _) = await UserWithKeyAsync();
        var privateNote = await CreateNoteAsync(owner, "Privada", "texto-secreto-de-A");
        var (intruder, intruderKey, _) = await UserWithKeyAsync();
        _api.Ai.ScriptSteps(intruderKey,
            Calls(("read_note", new { note_id = privateNote }), ("add_tags", new { note_id = privateNote, tags = new[] { "robada" } })),
            Text("No existe."));

        var answer = await AskAsync(intruder, "Lee y etiqueta la nota");

        Assert.Empty(answer.GetProperty("actions").EnumerateArray());
        Assert.Empty(answer.GetProperty("sources").EnumerateArray());
        Assert.Empty(await TagsOfAsync(owner, privateNote));
        var toolResults = _api.Ai.StepRequests.Where(r => r.Key == intruderKey).SelectMany(r => r.Messages).Where(m => m.Role == "tool").ToList();
        Assert.NotEmpty(toolResults);
        Assert.All(toolResults, m => Assert.Contains("not_found", m.Content));
        Assert.DoesNotContain(toolResults, m => m.Content!.Contains("texto-secreto-de-A"));
    }

    [Fact]
    public async Task Searches_are_made_with_the_asking_users_own_id()
    {
        var (_, _, ownerId) = await UserWithKeyAsync();
        var (client, key, userId) = await UserWithKeyAsync();
        _api.Ai.ScriptSteps(key, Calls(("search_notes", new { query = "nginx" })), Text("Nada."));

        await AskAsync(client, "¿Qué tengo sobre nginx?");

        Assert.Contains(_api.Ai.Searches, s => s.UserId == userId && s.Query == "nginx");
        Assert.DoesNotContain(_api.Ai.Searches, s => s.UserId == ownerId);
    }

    [Fact]
    public async Task One_users_memory_never_reaches_another_users_questions()
    {
        var (owner, _, _) = await UserWithKeyAsync();
        (await owner.PostAsJsonAsync("/api/memory", new { text = "memoria-privada-de-A" })).EnsureSuccessStatusCode();
        var (client, key, _) = await UserWithKeyAsync();
        (await client.PostAsJsonAsync("/api/memory", new { text = "uso Ubuntu" })).EnsureSuccessStatusCode();
        _api.Ai.ScriptSteps(key, Text("Hola."));

        await AskAsync(client, "hola");
        await AskAsync(client, "otra, respondida sin acciones"); // no steps left: the model "can't" use tools

        var system = _api.Ai.StepRequests.Where(r => r.Key == key).Select(r => r.Messages[0].Content!).ToList();
        Assert.All(system, s => Assert.Contains("- uso Ubuntu", s));
        Assert.DoesNotContain(system, s => s.Contains("memoria-privada-de-A"));
        var memory = Assert.Single(_api.Ai.Asks, a => a.Key == key).Memory;
        Assert.Equal(["uso Ubuntu"], memory!);
    }

    [Fact]
    public async Task Global_follow_ups_carry_the_conversations_history()
    {
        var (client, key, _) = await UserWithKeyAsync();
        _api.Ai.ScriptSteps(key, Text("Con systemctl."));

        await AskAsync(client, "¿y cómo lo reinicio?", [new { question = "¿cómo configuré nginx?", answer = "Como proxy inverso." }]);

        var messages = Assert.Single(_api.Ai.StepRequests, r => r.Key == key).Messages;
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("¿cómo configuré nginx?", messages[1].Content);
    }
}
