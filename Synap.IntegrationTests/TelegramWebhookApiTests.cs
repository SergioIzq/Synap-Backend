using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Reminders.Commands;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// assistant-reminders tasks 6.1 to 6.4 - the webhook through the real HTTP pipeline: the shared
/// secret, `/start &lt;code&gt;` linking, and every inline button.
/// </summary>
[Collection(ApiCollection.Name)]
public class TelegramWebhookApiTests
{
    private readonly ApiFixture _api;

    public TelegramWebhookApiTests(ApiFixture api) => _api = api;

    private static string NewChatId() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        return client.WithBearer(token);
    }

    /// <summary>A signed-in user whose Telegram chat is linked through the real flow.</summary>
    private async Task<(HttpClient Client, string ChatId, string? Timezone)> LinkedAsync(string? timezone = "Europe/Madrid")
    {
        var client = await SignedInAsync();
        var chatId = NewChatId();

        var start = await client.PostAsync("/api/settings/telegram/link", null);
        start.EnsureSuccessStatusCode();
        var code = (await start.ReadJsonAsync()).GetProperty("value").GetProperty("code").GetString();

        var linked = await SendUpdateAsync(Message(chatId, $"/start {code}"));
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        return (client, chatId, timezone);
    }

    private Task<HttpResponseMessage> SendUpdateAsync(object update, string? secret = ApiFixture.TelegramWebhookSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook") { Content = JsonContent.Create(update) };
        if (secret is not null)
        {
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        }

        return _api.CreateClient().SendAsync(request);
    }

    private static object Message(string chatId, string text)
        => new { message = new { message_id = 1, chat = new { id = long.Parse(chatId) }, text } };

    private static object Callback(string chatId, string data, long messageId = 77)
        => new { callback_query = new { id = "cb-1", data, message = new { message_id = messageId, chat = new { id = long.Parse(chatId) } } } };

    private async Task<Guid> CreateReminderAsync(
        HttpClient client, string text, DateTime dueAtUtc, string? recurrence = null, string? timezone = null)
    {
        // The browser's timezone travels with the write, as it does from the web app.
        var response = await client.PostAsJsonAsync("/api/reminders", new { text, dueAtUtc, recurrence, timezone });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetProperty("id").GetGuid();
    }

    private async Task<(DateTime DueAt, DateTime? SentAt, DateTime? DismissedAt, string? Recurrence)> RowAsync(Guid reminderId)
    {
        await using var connection = await _api.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(DateTime, DateTime?, DateTime?, string?)>(
            "SELECT due_at, sent_at, dismissed_at, recurrence FROM reminders WHERE id = @reminderId", new { reminderId });
    }

    /// <summary>Marks the reminder delivered for its current moment, as the poller would.</summary>
    private async Task<DateTime> MarkDeliveredAsync(Guid reminderId)
    {
        await using var connection = await _api.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<DateTime>(
            "UPDATE reminders SET sent_at = now() WHERE id = @reminderId RETURNING due_at", new { reminderId });
    }

    // ---- The shared secret (task 6.1) ----

    [Fact]
    public async Task A_call_without_the_secret_is_rejected()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await SendUpdateAsync(new { }, secret: null)).StatusCode);

    [Fact]
    public async Task A_call_with_the_wrong_secret_is_rejected()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await SendUpdateAsync(new { }, secret: "not-the-secret")).StatusCode);

    [Fact]
    public async Task An_update_with_the_right_secret_is_accepted_even_when_there_is_nothing_to_do()
    {
        Assert.Equal(HttpStatusCode.OK, (await SendUpdateAsync(new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendUpdateAsync(new { update_id = 1, channel_post = new { } })).StatusCode);
    }

    // ---- Linking (task 6.2) ----

    [Fact]
    public async Task The_start_code_links_the_chat_and_the_bot_says_so()
    {
        var (client, chatId, timezone) = await LinkedAsync();

        Assert.Contains(ProcessTelegramUpdateCommandHandler.LinkedMessage, _api.Telegram.To(chatId).Select(m => m.Text));

        var status = await (await client.GetAsync("/api/settings/telegram")).ReadJsonAsync();
        Assert.True(status.GetProperty("value").GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task A_bad_code_links_nothing_and_says_so_without_naming_anyone()
    {
        var chatId = NewChatId();

        await SendUpdateAsync(Message(chatId, "/start deadbeefdeadbeef"));

        var reply = Assert.Single(_api.Telegram.To(chatId));
        Assert.Equal(ProcessTelegramUpdateCommandHandler.LinkFailedMessage, reply.Text);
    }

    [Fact]
    public async Task A_code_only_works_once()
    {
        var client = await SignedInAsync();
        var code = (await (await client.PostAsync("/api/settings/telegram/link", null)).ReadJsonAsync())
            .GetProperty("value").GetProperty("code").GetString();
        var firstChat = NewChatId();
        var secondChat = NewChatId();

        await SendUpdateAsync(Message(firstChat, $"/start {code}"));
        await SendUpdateAsync(Message(secondChat, $"/start {code}"));

        Assert.Equal(ProcessTelegramUpdateCommandHandler.LinkedMessage, Assert.Single(_api.Telegram.To(firstChat)).Text);
        Assert.Equal(ProcessTelegramUpdateCommandHandler.LinkFailedMessage, Assert.Single(_api.Telegram.To(secondChat)).Text);
    }

    [Fact]
    public async Task Any_other_message_gets_the_bots_instructions()
    {
        var chatId = NewChatId();

        await SendUpdateAsync(Message(chatId, "hola bot"));

        Assert.Equal(ProcessTelegramUpdateCommandHandler.UnknownCommandMessage, Assert.Single(_api.Telegram.To(chatId)).Text);
    }

    // ---- Buttons (task 6.3) ----

    [Fact]
    public async Task Confirming_a_one_off_reminder_finishes_it_and_edits_the_message()
    {
        var (client, chatId, timezone) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Renovar el certificado SSL", DateTime.UtcNow.AddMinutes(5));
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.Confirm, id, dueAt)));

        var row = await RowAsync(id);
        Assert.NotNull(row.DismissedAt);
        Assert.Equal(dueAt, row.DueAt);
        Assert.Contains("✓ Hecho.", _api.Telegram.Edited.Select(e => e.Text));

        // No longer pending, so it is gone from the list.
        var list = await (await client.GetAsync("/api/reminders")).ReadJsonAsync();
        Assert.DoesNotContain(id, list.GetProperty("value").GetProperty("reminders").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Confirming_a_recurring_reminder_schedules_the_next_occurrence()
    {
        var (client, chatId, timezone) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Revisar copias", DateTime.UtcNow.AddMinutes(5), "daily");
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.Confirm, id, dueAt)));

        var row = await RowAsync(id);
        Assert.Equal(dueAt.AddDays(1), row.DueAt);
        Assert.Null(row.SentAt);
        Assert.Null(row.DismissedAt);
        Assert.Equal("daily", row.Recurrence);
    }

    [Fact]
    public async Task Confirming_the_same_message_twice_does_not_advance_the_series_again()
    {
        var (client, chatId, timezone) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Solo una vez", DateTime.UtcNow.AddMinutes(5), "daily");
        var dueAt = await MarkDeliveredAsync(id);
        var payload = ReminderMessage.Data(ReminderAction.Confirm, id, dueAt);

        await SendUpdateAsync(Callback(chatId, payload));
        var afterFirst = await RowAsync(id);
        await SendUpdateAsync(Callback(chatId, payload));

        // The stale payload's occurrence no longer matches the row, so the second press is ignored.
        Assert.Equal(afterFirst.DueAt, (await RowAsync(id)).DueAt);
    }

    [Fact]
    public async Task Snoozing_an_hour_moves_the_reminder_and_makes_it_pending_again()
    {
        var (client, chatId, timezone) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Más tarde", DateTime.UtcNow.AddMinutes(5));
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.SnoozeHour, id, dueAt)));

        var row = await RowAsync(id);
        Assert.Null(row.SentAt);
        Assert.InRange(row.DueAt, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(65));
    }

    [Fact]
    public async Task Cancelling_a_series_stops_it_for_good()
    {
        var (client, chatId, timezone) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Ya no más", DateTime.UtcNow.AddMinutes(5), "daily");
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.CancelSeries, id, dueAt)));

        var row = await RowAsync(id);
        Assert.NotNull(row.DismissedAt);
        Assert.NotNull(row.SentAt);
        Assert.Null(row.Recurrence);
        Assert.Contains("Serie cancelada", string.Join(" ", _api.Telegram.Edited.Select(e => e.Text)));
    }

    [Fact]
    public async Task A_button_pressed_from_an_unlinked_chat_changes_nothing()
    {
        var (client, _, _) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Privado", DateTime.UtcNow.AddMinutes(5));
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(NewChatId(), ReminderMessage.Data(ReminderAction.Confirm, id, dueAt)));

        Assert.Null((await RowAsync(id)).DismissedAt);
    }

    [Fact]
    public async Task A_button_for_someone_elses_reminder_changes_nothing()
    {
        var (owner, _, _) = await LinkedAsync();
        var (_, otherChat, _) = await LinkedAsync();
        var id = await CreateReminderAsync(owner, "De su dueño", DateTime.UtcNow.AddMinutes(5));
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(otherChat, ReminderMessage.Data(ReminderAction.Confirm, id, dueAt)));

        Assert.Null((await RowAsync(id)).DismissedAt);
    }

    [Fact]
    public async Task A_button_for_a_reminder_that_no_longer_exists_is_ignored()
    {
        var (_, chatId, _) = await LinkedAsync();

        var response = await SendUpdateAsync(
            Callback(chatId, ReminderMessage.Data(ReminderAction.Confirm, Guid.NewGuid(), DateTime.UtcNow)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_payload_is_ignored()
    {
        var (_, chatId, _) = await LinkedAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendUpdateAsync(Callback(chatId, "basura"))).StatusCode);
    }

    // ---- Disconnecting (task 8.2) ----

    [Fact]
    public async Task Disconnecting_leaves_the_reminders_pending_and_reports_it_in_the_status()
    {
        var (client, _, _) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Sigue pendiente", DateTime.UtcNow.AddMinutes(5));

        (await client.DeleteAsync("/api/settings/telegram")).EnsureSuccessStatusCode();

        var status = await (await client.GetAsync("/api/settings/telegram")).ReadJsonAsync();
        Assert.False(status.GetProperty("value").GetProperty("connected").GetBoolean());

        var row = await RowAsync(id);
        Assert.Null(row.SentAt);
        Assert.Null(row.DismissedAt);

        var list = await (await client.GetAsync("/api/reminders")).ReadJsonAsync();
        Assert.Contains(id, list.GetProperty("value").GetProperty("reminders").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()));
        Assert.False(list.GetProperty("value").GetProperty("telegramConnected").GetBoolean());
    }

    [Fact]
    public async Task A_due_reminder_is_not_delivered_once_telegram_is_disconnected()
    {
        var (client, chatId, _) = await LinkedAsync();
        await CreateReminderAsync(client, "No debe llegar", DateTime.UtcNow.AddMinutes(5));
        (await client.DeleteAsync("/api/settings/telegram")).EnsureSuccessStatusCode();
        _api.Telegram.Clear();

        // Due now, but its owner has no linked chat any more.
        await using (var connection = await _api.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE reminders SET due_at = now() - interval '1 minute' WHERE text = @text", new { text = "No debe llegar" });
        }

        // Driven directly rather than waiting on the poller's minute, through the host's own wiring.
        using var scope = _api.Services.CreateScope();
        var delivered = await scope.ServiceProvider.GetRequiredService<ReminderDeliveryService>()
            .DeliverDueAsync(DateTime.UtcNow);

        Assert.DoesNotContain(_api.Telegram.Sent, m => m.Text.Contains("No debe llegar"));
        Assert.Empty(_api.Telegram.To(chatId));
    }

    [Fact]
    public async Task Buttons_on_a_message_delivered_before_disconnecting_are_inert()
    {
        var (client, chatId, _) = await LinkedAsync();
        var id = await CreateReminderAsync(client, "Botón viejo", DateTime.UtcNow.AddMinutes(5));
        var dueAt = await MarkDeliveredAsync(id);
        (await client.DeleteAsync("/api/settings/telegram")).EnsureSuccessStatusCode();

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.Confirm, id, dueAt)));

        Assert.Null((await RowAsync(id)).DismissedAt);
    }

    [Fact]
    public async Task Connecting_again_after_disconnecting_needs_a_new_code()
    {
        var client = await SignedInAsync();
        var chatId = NewChatId();
        var oldCode = (await (await client.PostAsync("/api/settings/telegram/link", null)).ReadJsonAsync())
            .GetProperty("value").GetProperty("code").GetString();
        await SendUpdateAsync(Message(chatId, $"/start {oldCode}"));
        (await client.DeleteAsync("/api/settings/telegram")).EnsureSuccessStatusCode();
        _api.Telegram.Clear();

        await SendUpdateAsync(Message(chatId, $"/start {oldCode}"));
        Assert.Equal(ProcessTelegramUpdateCommandHandler.LinkFailedMessage, Assert.Single(_api.Telegram.To(chatId)).Text);

        var newCode = (await (await client.PostAsync("/api/settings/telegram/link", null)).ReadJsonAsync())
            .GetProperty("value").GetProperty("code").GetString();
        await SendUpdateAsync(Message(chatId, $"/start {newCode}"));

        var status = await (await client.GetAsync("/api/settings/telegram")).ReadJsonAsync();
        Assert.True(status.GetProperty("value").GetProperty("connected").GetBoolean());
    }

    // ---- Wall-clock snoozes (task 6.4) ----

    [Fact]
    public async Task Snoozing_to_tomorrow_lands_at_nine_in_the_users_own_timezone()
    {
        var (client, chatId, timezone) = await LinkedAsync("Europe/Madrid");
        var id = await CreateReminderAsync(client, "Mañana por la mañana", DateTime.UtcNow.AddMinutes(5), timezone: timezone);
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.SnoozeTomorrow, id, dueAt)));

        var row = await RowAsync(id);
        var inMadrid = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(row.DueAt, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));
        Assert.Equal(new TimeSpan(9, 0, 0), inMadrid.TimeOfDay);
        Assert.True(row.DueAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Snoozing_to_next_week_lands_at_nine_seven_days_out()
    {
        var (client, chatId, timezone) = await LinkedAsync("Europe/Madrid");
        var id = await CreateReminderAsync(client, "La semana que viene", DateTime.UtcNow.AddMinutes(5), timezone: timezone);
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.SnoozeNextWeek, id, dueAt)));

        var row = await RowAsync(id);
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var inMadrid = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.DueAt, DateTimeKind.Utc), madrid);
        var todayInMadrid = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, madrid).Date;
        Assert.Equal(new TimeSpan(9, 0, 0), inMadrid.TimeOfDay);
        Assert.Equal(todayInMadrid.AddDays(7), inMadrid.Date);
    }

    [Fact]
    public async Task A_user_with_no_stored_timezone_is_snoozed_in_utc()
    {
        var (client, chatId, timezone) = await LinkedAsync(timezone: null);
        var id = await CreateReminderAsync(client, "Sin huso horario", DateTime.UtcNow.AddMinutes(5), timezone: timezone);
        var dueAt = await MarkDeliveredAsync(id);

        await SendUpdateAsync(Callback(chatId, ReminderMessage.Data(ReminderAction.SnoozeTomorrow, id, dueAt)));

        var row = await RowAsync(id);
        Assert.Equal(new TimeSpan(9, 0, 0), row.DueAt.TimeOfDay);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), row.DueAt.Date);
    }
}
