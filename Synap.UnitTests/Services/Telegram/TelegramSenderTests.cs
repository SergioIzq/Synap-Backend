using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;
using Synap.Infrastructure.Services.Telegram;
using Synap.Shared.Application.Interfaces;
using System.Net;
using System.Text.Json;

namespace Synap.UnitTests.Services.Telegram;

/// <summary>assistant-reminders task 5.2.</summary>
public class TelegramSenderTests
{
    private static readonly TelegramMessage Message = new("123456789", "Renovar el certificado SSL",
    [
        new TelegramButton("✓ Hecho", "done:1"),
        new TelegramButton("⏰ En 1 hora", "snooze:1h"),
    ]);

    private static (TelegramSender Sender, RecordingHandler Handler) Build(
        TelegramSettings settings, HttpStatusCode status = HttpStatusCode.OK, string body = "{\"ok\":true}")
    {
        var handler = new RecordingHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        return (new TelegramSender(client, Options.Create(settings), NullLogger<TelegramSender>.Instance), handler);
    }

    private static TelegramSettings On(bool inlineButtons = true)
        => new() { Enabled = true, BotToken = "bot-token", InlineButtons = inlineButtons };

    [Fact]
    public async Task A_disabled_sender_makes_no_request_at_all()
    {
        var (sender, handler) = Build(new TelegramSettings { Enabled = false, BotToken = "bot-token" });

        Assert.False(sender.IsEnabled);
        Assert.False(await sender.SendAsync(Message));
        Assert.False(await sender.EditAsync("123", 7, "texto"));
        await sender.AnswerCallbackAsync("callback-id");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_sender_without_a_token_is_not_enabled()
    {
        var (sender, handler) = Build(new TelegramSettings { Enabled = true, BotToken = "" });

        Assert.False(sender.IsEnabled);
        Assert.False(await sender.SendAsync(Message));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Sending_posts_the_text_and_the_buttons_to_sendMessage()
    {
        var (sender, handler) = Build(On());

        Assert.True(await sender.SendAsync(Message));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/botbot-token/sendMessage", request.Path);
        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("123456789", payload.GetProperty("chat_id").GetString());
        Assert.Equal("Renovar el certificado SSL", payload.GetProperty("text").GetString());

        var rows = payload.GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("✓ Hecho", rows[0][0].GetProperty("text").GetString());
        Assert.Equal("done:1", rows[0][0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task Buttons_are_dropped_when_there_is_no_webhook_to_answer_them()
    {
        var (sender, handler) = Build(On(inlineButtons: false));

        Assert.True(await sender.SendAsync(Message));

        var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        Assert.Equal("Renovar el certificado SSL", payload.GetProperty("text").GetString());
        Assert.False(payload.TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task A_message_without_buttons_carries_no_keyboard()
    {
        var (sender, handler) = Build(On());

        Assert.True(await sender.SendAsync(new TelegramMessage("123", "sin botones")));

        Assert.False(JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task Editing_replaces_the_text_and_clears_the_buttons()
    {
        var (sender, handler) = Build(On());

        Assert.True(await sender.EditAsync("123456789", 42, "✓ Hecho"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/botbot-token/editMessageText", request.Path);
        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(42, payload.GetProperty("message_id").GetInt64());
        Assert.Equal("✓ Hecho", payload.GetProperty("text").GetString());
        Assert.Empty(payload.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
    }

    [Fact]
    public async Task A_blocked_bot_is_reported_as_a_failure_not_an_exception()
    {
        var (sender, _) = Build(On(), HttpStatusCode.Forbidden, "{\"ok\":false,\"description\":\"bot was blocked by the user\"}");

        Assert.False(await sender.SendAsync(Message));
    }

    [Fact]
    public async Task An_unknown_chat_is_reported_as_a_failure()
    {
        var (sender, _) = Build(On(), HttpStatusCode.BadRequest, "{\"ok\":false,\"description\":\"chat not found\"}");

        Assert.False(await sender.SendAsync(Message));
    }

    [Fact]
    public async Task An_unreachable_telegram_is_reported_as_a_failure()
    {
        var handler = new ThrowingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var sender = new TelegramSender(client, Options.Create(On()), NullLogger<TelegramSender>.Instance);

        Assert.False(await sender.SendAsync(Message));
    }

    [Fact]
    public async Task Acknowledging_a_button_press_posts_the_callback_id()
    {
        var (sender, handler) = Build(On());

        await sender.AnswerCallbackAsync("callback-id", "Hecho ✓");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/botbot-token/answerCallbackQuery", request.Path);
        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("callback-id", payload.GetProperty("callback_query_id").GetString());
        Assert.Equal("Hecho ✓", payload.GetProperty("text").GetString());
    }

    private sealed record SentRequest(string Path, string Body);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.RequestUri!.AbsolutePath,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }
}
