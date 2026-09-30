using System.Net;
using System.Net.Http.Json;
using Synap.Application.Features.Briefing;
using Synap.Application.Features.Reminders.Commands;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// daily-briefing tasks 5.3 to 5.5 - specs/briefing "A briefing can be asked for at any moment",
/// over HTTP, both from the web app and through the bot.
/// </summary>
[Collection(ApiCollection.Name)]
public class BriefingOnDemandApiTests
{
    private readonly ApiFixture _api;

    public BriefingOnDemandApiTests(ApiFixture api) => _api = api;

    private static string NewChatId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    private async Task<(HttpClient Client, string ChatId)> LinkedAsync()
    {
        var client = await SignedInAsync();
        var chatId = NewChatId();

        var start = await client.PostAsync("/api/settings/telegram/link", null);
        start.EnsureSuccessStatusCode();
        var code = (await start.ReadJsonAsync()).GetProperty("value").GetProperty("code").GetString();
        (await SendUpdateAsync(Message(chatId, $"/start {code}"))).EnsureSuccessStatusCode();

        return (client, chatId);
    }

    private Task<HttpResponseMessage> SendUpdateAsync(object update)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook") { Content = JsonContent.Create(update) };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", ApiFixture.TelegramWebhookSecret);
        return _api.CreateClient().SendAsync(request);
    }

    private static object Message(string chatId, string text)
        => new { message = new { message_id = 1, chat = new { id = long.Parse(chatId) }, text } };

    private static async Task GiveThemANoteAsync(HttpClient client)
        => (await client.PostAsJsonAsync("/api/notes", new { type = "Text", title = "Sin etiquetar", content = "algo que apunté" }))
            .EnsureSuccessStatusCode();

    /// <summary>The briefing itself, not the "linked" confirmation the /start flow sends.</summary>
    private IReadOnlyList<string> BriefingsTo(string chatId)
        => _api.Telegram.To(chatId)
            .Select(m => m.Text)
            .Where(t => t.Contains("Buenos días") || t == BriefingMessage.NothingToReport)
            .ToList();

    // ---- From the web app ----

    [Fact]
    public async Task Asking_from_the_web_app_sends_the_briefing_to_the_linked_chat()
    {
        var (client, chatId) = await LinkedAsync();
        await GiveThemANoteAsync(client);

        var response = await client.PostAsync("/api/settings/briefing/send", null);

        response.EnsureSuccessStatusCode();
        var briefing = Assert.Single(BriefingsTo(chatId));
        Assert.Contains("Sin etiquetar", briefing);
    }

    /// <summary>specs/briefing "Asked for with nothing to report".</summary>
    [Fact]
    public async Task Asking_on_a_day_with_nothing_to_report_still_answers()
    {
        var (client, chatId) = await LinkedAsync();

        (await client.PostAsync("/api/settings/briefing/send", null)).EnsureSuccessStatusCode();

        Assert.Equal(BriefingMessage.NothingToReport, Assert.Single(BriefingsTo(chatId)));
    }

    /// <summary>specs/briefing "Asked for before the briefing was ever turned on".</summary>
    [Fact]
    public async Task Asking_works_without_turning_the_briefing_on_and_starts_no_automatic_ones()
    {
        var (client, chatId) = await LinkedAsync();
        await GiveThemANoteAsync(client);

        (await client.PostAsync("/api/settings/briefing/send", null)).EnsureSuccessStatusCode();

        Assert.Single(BriefingsTo(chatId));
        var briefing = (await (await client.GetAsync("/api/settings")).ReadJsonAsync())
            .GetProperty("value").GetProperty("briefing");
        Assert.False(briefing.GetProperty("enabled").GetBoolean());
    }

    /// <summary>specs/briefing "Asked for without a linked chat".</summary>
    [Fact]
    public async Task Asking_without_a_linked_chat_is_refused_with_the_connect_message()
    {
        var client = await SignedInAsync();

        var response = await client.PostAsync("/api/settings/briefing/send", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Conecta Telegram", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asking_needs_authentication()
    {
        var response = await _api.CreateClient().PostAsync("/api/settings/briefing/send", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- From the bot ----

    /// <summary>specs/briefing "Asked for from the bot".</summary>
    [Fact]
    public async Task The_bot_command_sends_the_briefing_to_the_chat_that_asked()
    {
        var (client, chatId) = await LinkedAsync();
        await GiveThemANoteAsync(client);

        var response = await SendUpdateAsync(Message(chatId, "/briefing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sin etiquetar", Assert.Single(BriefingsTo(chatId)));
    }

    /// <summary>specs/briefing "The bot is asked by an unknown chat".</summary>
    [Fact]
    public async Task The_bot_tells_an_unknown_chat_nothing()
    {
        var stranger = NewChatId();

        var response = await SendUpdateAsync(Message(stranger, "/briefing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(BriefingsTo(stranger));
        // The same reply anything unrecognised gets: no hint that an account does or does not exist.
        var reply = Assert.Single(_api.Telegram.To(stranger));
        Assert.Equal(ProcessTelegramUpdateCommandHandler.UnknownCommandMessage, reply.Text);
    }

    [Fact]
    public async Task The_bot_command_does_not_consume_the_automatic_briefing()
    {
        var (client, chatId) = await LinkedAsync();
        await GiveThemANoteAsync(client);
        (await client.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 9 })).EnsureSuccessStatusCode();

        (await SendUpdateAsync(Message(chatId, "/briefing"))).EnsureSuccessStatusCode();

        // The setting is untouched, so the sweep still owes them today's.
        var briefing = (await (await client.GetAsync("/api/settings")).ReadJsonAsync())
            .GetProperty("value").GetProperty("briefing");
        Assert.True(briefing.GetProperty("enabled").GetBoolean());
        Assert.Single(BriefingsTo(chatId));
    }

    /// <summary>The command the bot already knew must keep working exactly as it did.</summary>
    [Fact]
    public async Task An_unrecognised_message_still_gets_the_usual_reply()
    {
        var chatId = NewChatId();

        (await SendUpdateAsync(Message(chatId, "hola"))).EnsureSuccessStatusCode();

        Assert.Equal(
            ProcessTelegramUpdateCommandHandler.UnknownCommandMessage,
            Assert.Single(_api.Telegram.To(chatId)).Text);
    }
}
