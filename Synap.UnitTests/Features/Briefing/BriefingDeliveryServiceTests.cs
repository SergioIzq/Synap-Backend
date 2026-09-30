using Microsoft.Extensions.Logging.Abstractions;
using Synap.Application.Features.Briefing;
using Synap.Application.Features.Reminders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Reminders;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// daily-briefing tasks 4.2 to 4.5, 4.7 and 4.8 - the sweep, with a controllable clock and no
/// Telegram. specs/briefing "A briefing is sent once a day at the hour the user chose" and
/// "A briefing that cannot be delivered says so rather than vanishing".
/// </summary>
public class BriefingDeliveryServiceTests
{
    private const string Madrid = "Europe/Madrid";
    private const string ChatId = "123456789";

    /// <summary>10:00 in Madrid, 02:00 in Mexico City, 08:00 UTC.</summary>
    private static readonly DateTime MorningUtc = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserRepository _users = new();
    private readonly FakeBriefingReadRepository _content = new();
    private readonly FakeTelegramSender _telegram = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly WithheldBriefingRecorder _withheld = new();
    private readonly RecordingLogger<BriefingDeliveryService> _logger = new();

    private BriefingDeliveryService Service() => new(
        _users,
        new BriefingDispatcher(new BriefingContentService(_content), _telegram),
        _telegram,
        _unitOfWork,
        _withheld,
        _logger);

    private User Subscriber(int hour = 9, string? timezone = Madrid, string? chatId = ChatId)
    {
        var user = User.Create(
            Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        user.SetTimezone(timezone);
        user.SetBriefing(enabled: true, hour);
        if (chatId is not null)
        {
            user.CompleteTelegramLink(chatId);
        }

        _users.Add(user);
        return user;
    }

    /// <summary>Something to report, so a briefing is worth sending.</summary>
    private void GiveThemSomething(string title = "Sin etiquetar")
        => _content.UntaggedFor = _ => new BriefingSection<BriefingNote>([new BriefingNote(Guid.NewGuid(), title, "algo")], 1);

    /// <summary>Something to report, different per user, so a test can tell their messages apart.</summary>
    private void GiveEachTheirOwn()
        => _content.UntaggedFor = userId => new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), $"Nota de {userId}", "algo")], 1);

    private static DateOnly LocalDayOf(DateTime utc, string? timezone)
        => DateOnly.FromDateTime(UserClock.ToLocal(utc, timezone));

    // ---- 4.2 Sending, once ----

    [Fact]
    public async Task A_user_whose_hour_has_come_is_briefed_and_the_day_is_recorded()
    {
        var user = Subscriber();
        GiveThemSomething();

        var sent = await Service().SweepAsync(MorningUtc);

        Assert.Equal(1, sent);
        Assert.Equal(ChatId, Assert.Single(_telegram.Sent).ChatId);
        Assert.Contains("Sin etiquetar", _telegram.Sent[0].Text);
        Assert.Equal(LocalDayOf(MorningUtc, Madrid), user.BriefingLastResolvedOn);
        Assert.True(_unitOfWork.SaveCalls > 0);
    }

    /// <summary>specs/briefing "Not sent twice in a day".</summary>
    [Fact]
    public async Task A_second_sweep_the_same_local_day_sends_nothing()
    {
        Subscriber();
        GiveThemSomething();

        await Service().SweepAsync(MorningUtc);
        await Service().SweepAsync(MorningUtc.AddHours(3));

        Assert.Single(_telegram.Sent);
    }

    /// <summary>specs/briefing "Before the chosen hour".</summary>
    [Fact]
    public async Task Before_the_chosen_hour_nothing_is_sent_and_the_day_stays_open()
    {
        var user = Subscriber(hour: 11);
        GiveThemSomething();

        await Service().SweepAsync(MorningUtc);

        Assert.Empty(_telegram.Sent);
        Assert.Null(user.BriefingLastResolvedOn);
    }

    [Fact]
    public async Task A_user_who_never_turned_it_on_is_never_considered()
    {
        var user = User.Create(Email.CreateFromDatabase("off@example.com"), PasswordHash.CreateFromDatabase("hash"));
        user.SetTimezone(Madrid);
        user.CompleteTelegramLink(ChatId);
        _users.Add(user);
        GiveThemSomething();

        await Service().SweepAsync(MorningUtc);

        Assert.Empty(_telegram.Sent);
    }

    // ---- 4.3 Nothing to report ----

    /// <summary>specs/briefing "Empty day" and "Items appear later the same day".</summary>
    [Fact]
    public async Task An_empty_day_sends_nothing_and_is_still_settled()
    {
        var user = Subscriber();

        var sent = await Service().SweepAsync(MorningUtc);

        Assert.Equal(0, sent);
        Assert.Empty(_telegram.Sent);
        Assert.Equal(LocalDayOf(MorningUtc, Madrid), user.BriefingLastResolvedOn);

        // Something turns up later the same day: it waits for tomorrow's briefing.
        GiveThemSomething();
        await Service().SweepAsync(MorningUtc.AddHours(5));

        Assert.Empty(_telegram.Sent);
    }

    // ---- 4.4 Failures ----

    /// <summary>specs/briefing "Delivery fails" - unresolved on purpose, so it is retried today.</summary>
    [Fact]
    public async Task A_failed_delivery_leaves_the_day_open_and_is_recorded()
    {
        var user = Subscriber();
        GiveThemSomething();
        _telegram.FailFor = _ => true;

        await Service().SweepAsync(MorningUtc);

        Assert.Null(user.BriefingLastResolvedOn);
        Assert.Contains(_logger.Records, r => r.Contains("could not be delivered"));

        // The next sweep that same day gets through.
        _telegram.FailFor = _ => false;
        await Service().SweepAsync(MorningUtc.AddMinutes(15));

        Assert.Single(_telegram.Sent);
        Assert.Equal(LocalDayOf(MorningUtc, Madrid), user.BriefingLastResolvedOn);
    }

    /// <summary>specs/briefing "One failure does not stop the rest".</summary>
    [Fact]
    public async Task One_users_failure_does_not_cost_the_others_their_briefing()
    {
        var failing = Subscriber();
        var fine = Subscriber(chatId: "999");
        GiveEachTheirOwn();
        _telegram.FailFor = text => text.Contains(failing.Id.Value.ToString());

        await Service().SweepAsync(MorningUtc);

        Assert.Single(_telegram.Sent);
        Assert.NotNull(fine.BriefingLastResolvedOn);
    }

    // ---- 4.5 Withheld for want of a chat ----

    /// <summary>specs/briefing "Briefing on, Telegram not connected" - said once, not every sweep.</summary>
    [Fact]
    public async Task A_subscriber_with_no_chat_is_recorded_once_and_the_day_stays_open()
    {
        var user = Subscriber(chatId: null);
        GiveThemSomething();

        await Service().SweepAsync(MorningUtc);
        await Service().SweepAsync(MorningUtc.AddMinutes(15));

        Assert.Empty(_telegram.Sent);
        Assert.Null(user.BriefingLastResolvedOn);
        Assert.Single(_logger.Records, r => r.Contains("withheld"));

        // Linking a chat later the same day still gets them today's briefing.
        user.CompleteTelegramLink(ChatId);
        await Service().SweepAsync(MorningUtc.AddMinutes(30));

        Assert.Single(_telegram.Sent);
    }

    // ---- 4.6 Delivery turned off for the deployment ----

    [Fact]
    public async Task With_delivery_off_nothing_is_sent_and_no_day_is_settled()
    {
        var user = Subscriber();
        GiveThemSomething();
        _telegram.Enabled = false;

        var sent = await Service().SweepAsync(MorningUtc);

        Assert.Equal(0, sent);
        Assert.Empty(_telegram.Sent);
        Assert.Null(user.BriefingLastResolvedOn);
    }

    // ---- 4.7 The hour that was missed, and the day that is over ----

    /// <summary>specs/briefing "The system was down at the hour".</summary>
    [Fact]
    public async Task An_hour_missed_earlier_today_is_still_briefed_when_the_sweep_returns()
    {
        var user = Subscriber(hour: 7);
        GiveThemSomething();

        // First sweep of the day happens at 18:00 local, eleven hours late.
        await Service().SweepAsync(MorningUtc.AddHours(8));

        Assert.Single(_telegram.Sent);
        Assert.Equal(LocalDayOf(MorningUtc, Madrid), user.BriefingLastResolvedOn);
    }

    /// <summary>specs/briefing "The day is over" - no backlog of missed days.</summary>
    [Fact]
    public async Task Yesterdays_briefing_is_never_caught_up()
    {
        var user = Subscriber();
        GiveThemSomething();

        // The sweep first runs two days on: one briefing, for today.
        var twoDaysOn = MorningUtc.AddDays(2);
        await Service().SweepAsync(twoDaysOn);

        Assert.Single(_telegram.Sent);
        Assert.Equal(LocalDayOf(twoDaysOn, Madrid), user.BriefingLastResolvedOn);
    }

    // ---- 4.8 Each user on their own clock ----

    /// <summary>specs/briefing "Each user on their own clock".</summary>
    [Fact]
    public async Task The_same_chosen_hour_briefs_each_user_when_it_comes_round_where_they_are()
    {
        var madrid = Subscriber(hour: 9, timezone: Madrid, chatId: "madrid");
        var mexico = Subscriber(hour: 9, timezone: "America/Mexico_City", chatId: "mexico");
        GiveThemSomething();

        // 08:00 UTC: 10:00 in Madrid, 02:00 in Mexico City.
        await Service().SweepAsync(MorningUtc);

        Assert.Equal(["madrid"], _telegram.Sent.Select(m => m.ChatId));
        Assert.NotNull(madrid.BriefingLastResolvedOn);
        Assert.Null(mexico.BriefingLastResolvedOn);

        // Seven hours on it is 09:00 in Mexico City, and only they are owed one.
        await Service().SweepAsync(MorningUtc.AddHours(7));

        Assert.Equal(["madrid", "mexico"], _telegram.Sent.Select(m => m.ChatId));
    }

    private sealed class FakeBriefingReadRepository : IBriefingReadRepository
    {
        public Func<Guid, BriefingSection<BriefingNote>> UntaggedFor { get; set; } = _ => BriefingSection<BriefingNote>.Empty;

        public Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
            Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(BriefingSection<BriefingReminder>.Empty);

        public Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
            Guid userId, DateTime sinceUtc, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(UntaggedFor(userId));

        public Task<BriefingSection<BriefingNote>> ListOpenThreadNotesAsync(
            Guid userId,
            IReadOnlyList<string> stemmedMarkers,
            IReadOnlyList<string> literalMarkers,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(BriefingSection<BriefingNote>.Empty);
    }
}
