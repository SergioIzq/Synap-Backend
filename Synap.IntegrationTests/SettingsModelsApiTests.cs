using Synap.Domain;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>assistant-agent-foundations task 6.1 - specs/user-settings "Action support shown in the picker" over HTTP.</summary>
[Collection(ApiCollection.Name)]
public class SettingsModelsApiTests
{
    private readonly ApiFixture _api;

    public SettingsModelsApiTests(ApiFixture api) => _api = api;

    [Fact]
    public async Task Model_list_marks_which_models_support_actions()
    {
        var key = $"gsk_models_{Guid.NewGuid():N}";
        _api.Ai.AcceptKey(key, new LlmModel("capable/model", true), new LlmModel("plain/model", false));
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        (await client.PutAsJsonAsync("/api/settings/ai/groq-key", new { apiKey = key })).EnsureSuccessStatusCode();

        var models = (await (await client.GetAsync("/api/settings/ai/models")).ReadJsonAsync()).GetProperty("value");

        var byId = models.EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("supportsActions").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["capable/model"] = true, ["plain/model"] = false }, byId);
    }
}
