using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;
using Synap.Shared.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synap.Infrastructure.Services.Telegram;

/// <summary>
/// The Telegram Bot API over a typed HttpClient (assistant-reminders design.md Decision 4). Never
/// throws for a delivery problem: an invalid chat, a blocked bot or an unreachable Telegram all
/// come back as false, so the poller leaves the reminder pending and retries on a later tick
/// (specs/reminders "Delivery failures do not lose a reminder").
///
/// Inline buttons are dropped when <see cref="TelegramSettings.InlineButtons"/> is off, for local
/// development without a webhook tunnel: pressing a button there would go nowhere, so the message
/// is sent as plain text rather than offering options that do nothing (design.md Risks).
/// </summary>
public sealed class TelegramSender : ITelegramSender
{
    private readonly HttpClient _httpClient;
    private readonly TelegramSettings _settings;
    private readonly ILogger<TelegramSender> _logger;

    public TelegramSender(HttpClient httpClient, IOptions<TelegramSettings> settings, ILogger<TelegramSender> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsEnabled => _settings.IsConfigured;

    public async Task<bool> SendAsync(TelegramMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["chat_id"] = message.ChatId,
            ["text"] = message.Text,
            ["parse_mode"] = "HTML",
            // The note link is already in the text; a preview card would bury the reminder itself.
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };

        if (message.Buttons.Count > 0 && _settings.InlineButtons)
        {
            payload["reply_markup"] = Keyboard(message.Buttons);
        }

        return await CallAsync("sendMessage", payload, cancellationToken);
    }

    public async Task<bool> EditAsync(string chatId, long messageId, string text, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            // Omitting reply_markup leaves the old buttons in place, so it is cleared explicitly.
            ["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() },
        };

        return await CallAsync("editMessageText", payload, cancellationToken);
    }

    public async Task AnswerCallbackAsync(string callbackId, string? toast = null, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var payload = new JsonObject { ["callback_query_id"] = callbackId };
        if (!string.IsNullOrWhiteSpace(toast))
        {
            payload["text"] = toast;
        }

        await CallAsync("answerCallbackQuery", payload, cancellationToken);
    }

    /// <summary>One row per button: the labels are long enough that a grid would wrap badly on a phone.</summary>
    private static JsonObject Keyboard(IReadOnlyList<TelegramButton> buttons)
    {
        var rows = new JsonArray();
        foreach (var button in buttons)
        {
            rows.Add(new JsonArray(new JsonObject
            {
                ["text"] = button.Label,
                ["callback_data"] = button.Data,
            }));
        }

        return new JsonObject { ["inline_keyboard"] = rows };
    }

    private async Task<bool> CallAsync(string method, JsonObject payload, CancellationToken cancellationToken)
    {
        try
        {
            // The leading "/" is load-bearing: a bot token is "<id>:<secret>", so a relative path
            // starting with "bot123:..." parses as a URI whose scheme is "bot123", and the request
            // fails with NotSupportedException before it ever leaves the process.
            using var response = await _httpClient.PostAsJsonAsync($"/bot{_settings.BotToken}/{method}", payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Telegram explains itself in the body ("bot was blocked by the user", "chat not
            // found"): worth logging, since it is the only trace of why a reminder never arrived.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Telegram {Method} was refused with {Status}: {Body}", method, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception exception) when (exception is NotSupportedException or UriFormatException or InvalidOperationException or JsonException)
        {
            // Nothing left the process: the request could not even be built. A bot token whose
            // colon makes the path parse as a URI scheme lands here, and for a whole afternoon it
            // was reported as Telegram being unreachable while no packet had ever been sent
            // (specs/platform-operations "A recorded failure names its own cause").
            _logger.LogWarning(
                exception, "Telegram {Method} was never sent: the request could not be built ({Exception})", method, exception.GetType().Name);
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout arrives as a cancellation nobody asked for. Distinguished
            // from the caller cancelling, which must still propagate.
            _logger.LogWarning("Telegram {Method} timed out after {Timeout}", method, _httpClient.Timeout);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception, "Telegram {Method} could not be reached: {Exception}", method, exception.GetType().Name);
            return false;
        }
    }
}
