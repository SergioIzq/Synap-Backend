using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Reminders.Commands;
using System.Text.Json;

namespace Synap.Api.Controllers;

/// <summary>
/// The Telegram bot's webhook (assistant-reminders design.md Decision 4). Anonymous - Telegram
/// can't hold a Synap session - so the shared secret Telegram echoes in a header is what proves the
/// call came from Telegram.
///
/// Always answers 200, even for an update it ignores or one that fails: a non-2xx makes Telegram
/// retry the same update for hours.
/// </summary>
[ApiController]
[Route("api/telegram")]
[AllowAnonymous]
public class TelegramController : ControllerBase
{
    /// <summary>The header Telegram sends when the webhook was registered with a secret_token.</summary>
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    private readonly ISender _sender;
    private readonly TelegramSettings _settings;

    public TelegramController(ISender sender, IOptions<TelegramSettings> settings)
    {
        _sender = sender;
        _settings = settings.Value;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement update)
    {
        if (!IsFromTelegram())
        {
            // 401 rather than 200: this is not Telegram, so there is no retry to worry about.
            return Unauthorized();
        }

        var parsed = Parse(update);
        if (parsed is not null)
        {
            await _sender.Send(new ProcessTelegramUpdateCommand(parsed));
        }

        return Ok();
    }

    /// <summary>
    /// A configured secret must match exactly. With delivery turned off the endpoint accepts
    /// nothing at all: there is no bot, so any call is noise.
    /// </summary>
    private bool IsFromTelegram()
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.WebhookSecret))
        {
            return false;
        }

        return Request.Headers.TryGetValue(SecretHeader, out var sent)
            && CryptographicEquals(sent.ToString(), _settings.WebhookSecret);
    }

    private static bool CryptographicEquals(string sent, string expected)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// Only the two shapes Synap acts on, out of Telegram's much larger update object: a text
    /// message, or a press on an inline button. Null for anything else (a photo, a channel post...).
    /// </summary>
    private static TelegramUpdate? Parse(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (update.TryGetProperty("callback_query", out var callback) && callback.ValueKind == JsonValueKind.Object)
        {
            var message = Property(callback, "message");
            var chatId = ChatIdOf(message);
            var id = String(callback, "id");
            if (chatId is null || id is null || message is null || !message.Value.TryGetProperty("message_id", out var messageId))
            {
                return null;
            }

            return new TelegramUpdate(null, new TelegramCallback(id, chatId, messageId.GetInt64(), String(callback, "data")));
        }

        if (update.TryGetProperty("message", out var incoming) && incoming.ValueKind == JsonValueKind.Object)
        {
            var chatId = ChatIdOf(incoming);
            return chatId is null ? null : new TelegramUpdate(new TelegramIncomingMessage(chatId, String(incoming, "text")), null);
        }

        return null;
    }

    /// <summary>Chat ids are numbers on the wire but only ever compared and echoed back as text.</summary>
    private static string? ChatIdOf(JsonElement? message)
    {
        if (Property(message ?? default, "chat") is not { } chat || !chat.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetInt64().ToString(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
