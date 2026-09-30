using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Users;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Reminders;

/// <summary>assistant-reminders task 5.4.</summary>
public class ReminderDeliveryServiceTests
{
    private const string ChatId = "123456789";
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Owner = UserId.Create(Guid.NewGuid()).Value;

    private readonly DueRemindersStub _reminders = new();
    private readonly FakeTelegramSender _telegram = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private readonly WithheldReminderRecorder _withheld = new();
    private readonly RecordingLogger<ReminderDeliveryService> _logger = new();

    private ReminderDeliveryService Service() => new(
        _reminders, _telegram, _unitOfWork,
        Options.Create(new AppOptions { PublicBaseUrl = "https://synap.sergioizq.com" }),
        _withheld,
        _logger);

    /// <summary>
    /// specs/reminders "Due with no chat linked": said once, not on every sweep. Before this, a
    /// reminder for a user without Telegram was skipped in complete silence - indistinguishable
    /// from one that was never created.
    /// </summary>
    [Fact]
    public async Task A_reminder_withheld_for_lack_of_a_chat_is_recorded_once_not_every_sweep()
    {
        Due("Renovar el certificado", chatId: null);

        await Service().DeliverDueAsync(Now.AddMinutes(2));
        await Service().DeliverDueAsync(Now.AddMinutes(3));

        var withheld = _logger.Records.Where(r => r.Contains("withheld")).ToList();
        Assert.Single(withheld);
        Assert.Contains("no linked Telegram chat", withheld[0]);
        Assert.Empty(_telegram.Sent);
    }

    private Reminder Due(string text, string? chatId = ChatId, string? recurrence = null)
    {
        var reminder = Reminder.Create(Owner, text, Now.AddMinutes(1), Now, recurrence: recurrence).Value;
        _reminders.Due.Add(new DueReminder(reminder, chatId, "Europe/Madrid", null));
        return reminder;
    }

    [Fact]
    public async Task Everything_due_is_delivered_and_marked()
    {
        var first = Due("Uno");
        var second = Due("Dos");

        var delivered = await Service().DeliverDueAsync(Now.AddMinutes(2));

        Assert.Equal(2, delivered);
        Assert.Equal(["Uno", "Dos"], _telegram.Sent.Select(m => Unwrap(m.Text)));
        Assert.All([first, second], r => Assert.False(r.IsPending));
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Nothing_is_delivered_or_even_read_while_delivery_is_turned_off()
    {
        Due("Uno");
        _telegram.Enabled = false;

        var delivered = await Service().DeliverDueAsync(Now.AddMinutes(2));

        Assert.Equal(0, delivered);
        Assert.Empty(_telegram.Sent);
        Assert.Equal(0, _reminders.ListCalls);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task A_reminder_of_a_user_with_no_linked_chat_stays_pending()
    {
        var unlinked = Due("Sin chat", chatId: null);

        var delivered = await Service().DeliverDueAsync(Now.AddMinutes(2));

        Assert.Equal(0, delivered);
        Assert.Empty(_telegram.Sent);
        Assert.True(unlinked.IsPending);
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest_and_leaves_that_one_pending()
    {
        var failing = Due("Falla");
        var fine = Due("Va bien");
        _telegram.FailFor = text => Unwrap(text) == "Falla";

        var delivered = await Service().DeliverDueAsync(Now.AddMinutes(2));

        Assert.Equal(1, delivered);
        Assert.True(failing.IsPending);
        Assert.False(fine.IsPending);
    }

    [Fact]
    public async Task A_failed_reminder_is_delivered_on_a_later_tick()
    {
        var reminder = Due("Reintento");
        _telegram.FailFor = _ => true;

        Assert.Equal(0, await Service().DeliverDueAsync(Now.AddMinutes(2)));
        Assert.True(reminder.IsPending);

        _telegram.FailFor = _ => false;

        Assert.Equal(1, await Service().DeliverDueAsync(Now.AddMinutes(3)));
        Assert.False(reminder.IsPending);
    }

    [Fact]
    public async Task Nothing_is_saved_when_nothing_was_delivered()
    {
        Due("Falla");
        _telegram.FailFor = _ => true;

        await Service().DeliverDueAsync(Now.AddMinutes(2));

        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task The_gap_between_sends_is_awaited_once_per_extra_message()
    {
        Due("Uno");
        Due("Dos");
        Due("Tres");
        var gaps = 0;

        await Service().DeliverDueAsync(Now.AddMinutes(2), _ =>
        {
            gaps++;
            return Task.CompletedTask;
        });

        // Three messages, two gaps: nothing is waited before the first.
        Assert.Equal(2, gaps);
    }

    [Fact]
    public async Task A_recurring_reminder_is_delivered_with_its_cancel_option()
    {
        Due("Revisar copias", recurrence: "daily");

        await Service().DeliverDueAsync(Now.AddMinutes(2));

        var message = Assert.Single(_telegram.Sent);
        Assert.Contains(message.Buttons, b => b.Label == "✗ Cancelar serie");
        Assert.Contains(message.Buttons, b => b.Label == "✓ Esta vez");
    }

    [Fact]
    public async Task An_empty_tick_touches_nothing()
    {
        Assert.Equal(0, await Service().DeliverDueAsync(Now));
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    /// <summary>The reminder text out of the formatted message body.</summary>
    private static string Unwrap(string text)
    {
        var start = text.IndexOf("<b>", StringComparison.Ordinal) + 3;
        return text[start..text.IndexOf("</b>", StringComparison.Ordinal)];
    }

    /// <summary>Only the poller's read matters here; the rest of the repository is unused.</summary>
    private sealed class DueRemindersStub : IReminderWriteRepository
    {
        public List<DueReminder> Due { get; } = [];
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<DueReminder>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<DueReminder>>(Due
                .Where(d => d.Reminder.DueAt <= nowUtc && d.Reminder.IsPending)
                .Take(limit)
                .ToList());
        }

        public Task<Reminder?> GetOwnedByUserAsync(Guid reminderId, Guid userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Reminder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Reminder entity)
        {
        }

        public Task CreateAsync(Reminder entity, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Update(Reminder entity)
        {
        }

        public void Delete(Reminder entity)
        {
        }
    }
}

internal sealed class FakeTelegramSender : ITelegramSender
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which messages fail, by their text; nothing fails by default.</summary>
    public Func<string, bool> FailFor { get; set; } = _ => false;

    public List<TelegramMessage> Sent { get; } = [];
    public List<(string ChatId, long MessageId, string Text)> Edited { get; } = [];
    public List<string> Acknowledged { get; } = [];

    public bool IsEnabled => Enabled;

    public Task<bool> SendAsync(TelegramMessage message, CancellationToken cancellationToken = default)
    {
        if (FailFor(message.Text))
        {
            return Task.FromResult(false);
        }

        Sent.Add(message);
        return Task.FromResult(true);
    }

    public Task<bool> EditAsync(string chatId, long messageId, string text, CancellationToken cancellationToken = default)
    {
        Edited.Add((chatId, messageId, text));
        return Task.FromResult(true);
    }

    public Task AnswerCallbackAsync(string callbackId, string? toast = null, CancellationToken cancellationToken = default)
    {
        Acknowledged.Add(callbackId);
        return Task.CompletedTask;
    }
}
