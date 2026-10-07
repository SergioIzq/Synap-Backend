using Synap.Application.Features.Briefing;
using Synap.Application.Features.Briefing.Commands;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;
using Synap.UnitTests.Features.Reminders;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// daily-briefing tasks 5.2, 5.3 and 5.5 - specs/briefing "A briefing can be asked for at any
/// moment". The bot's own path is covered over HTTP in Synap.IntegrationTests.
/// </summary>
public class BriefingOnDemandTests
{
    private const string Madrid = "Europe/Madrid";
    private const string ChatId = "123456789";

    private readonly FakeUserRepository _users = new();
    private readonly FakeBriefingRead _content = new();
    private readonly FakeTelegramSender _telegram = new();
    private readonly User _user;

    public BriefingOnDemandTests()
    {
        _user = User.Create(Email.CreateFromDatabase("me@example.com"), PasswordHash.CreateFromDatabase("hash"));
        _user.SetTimezone(Madrid);
        _user.CompleteTelegramLink(ChatId);
        _users.Add(_user);
    }

    private SendBriefingNowCommandHandler Handler() => new(
        _users,
        new BriefingDispatcher(new BriefingContentService(_content), _telegram),
        new FakeUserContext(_user.Id.Value));

    private void GiveThemSomething()
        => _content.Untagged = new BriefingSection<BriefingNote>([new BriefingNote(Guid.NewGuid(), "Sin etiquetar", "algo")], 1);

    /// <summary>specs/briefing "Asked for from the web app".</summary>
    [Fact]
    public async Task Asking_sends_the_briefing_to_the_users_chat()
    {
        GiveThemSomething();

        var result = await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(ChatId, Assert.Single(_telegram.Sent).ChatId);
        Assert.Contains("Sin etiquetar", _telegram.Sent[0].Text);
    }

    /// <summary>specs/briefing "Asking does not consume the day".</summary>
    [Fact]
    public async Task Asking_never_settles_the_day_so_the_automatic_one_still_arrives()
    {
        _user.SetBriefing(enabled: true, hour: 9);
        GiveThemSomething();

        await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.Null(_user.BriefingLastResolvedOn);
    }

    /// <summary>specs/briefing "Asked for twice".</summary>
    [Fact]
    public async Task Asking_again_sends_it_again()
    {
        GiveThemSomething();

        await Handler().Handle(new SendBriefingNowCommand(), default);
        await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.Equal(2, _telegram.Sent.Count);
    }

    /// <summary>
    /// specs/briefing "Asked for with nothing to report" - the one thing the on-demand path does
    /// that the automatic one must not: answer on an empty day.
    /// </summary>
    [Fact]
    public async Task Asking_on_an_empty_day_answers_instead_of_staying_silent()
    {
        var result = await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(BriefingMessage.NothingToReport, Assert.Single(_telegram.Sent).Text);
    }

    /// <summary>specs/briefing "Asked for before the briefing was ever turned on".</summary>
    [Fact]
    public async Task Asking_works_without_ever_turning_the_briefing_on_and_starts_no_automatic_ones()
    {
        GiveThemSomething();

        var result = await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.Single(_telegram.Sent);
        Assert.False(_user.BriefingEnabled);
        Assert.Null(_user.BriefingHour);
    }

    /// <summary>specs/briefing "Asked for without a linked chat".</summary>
    [Fact]
    public async Task Asking_without_a_linked_chat_says_to_connect_telegram()
    {
        _user.DisconnectTelegram();
        GiveThemSomething();

        var result = await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.True(result.IsFailure);
        Assert.Equal(BriefingErrors.TelegramNotConnected, result.Error);
        Assert.Empty(_telegram.Sent);
    }

    [Fact]
    public async Task A_delivery_that_fails_is_reported_as_such()
    {
        GiveThemSomething();
        _telegram.FailFor = _ => true;

        var result = await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.True(result.IsFailure);
        Assert.Equal(BriefingErrors.NotDelivered, result.Error);
    }

    /// <summary>specs/briefing "A requested briefing never crosses users".</summary>
    [Fact]
    public async Task Only_the_asking_users_own_data_is_ever_asked_for()
    {
        GiveThemSomething();

        await Handler().Handle(new SendBriefingNowCommand(), default);

        Assert.Equal([_user.Id.Value], _content.AskedFor.Distinct());
    }

    private sealed class FakeBriefingRead : IBriefingReadRepository
    {
        public BriefingSection<BriefingNote> Untagged { get; set; } = BriefingSection<BriefingNote>.Empty;

        public List<Guid> AskedFor { get; } = [];

        public Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
            Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken cancellationToken = default)
        {
            AskedFor.Add(userId);
            return Task.FromResult(BriefingSection<BriefingReminder>.Empty);
        }

        public Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
            Guid userId, DateTime sinceUtc, int limit, CancellationToken cancellationToken = default)
        {
            AskedFor.Add(userId);
            return Task.FromResult(Untagged);
        }

        public Task<BriefingSection<BriefingNote>> ListInProgressNotesAsync(
            Guid userId, int limit, CancellationToken cancellationToken = default)
        {
            AskedFor.Add(userId);
            return Task.FromResult(BriefingSection<BriefingNote>.Empty);
        }

        public Task<BriefingSection<BriefingNote>> ListPendingNotesAsync(
            Guid userId, DateTime pausedBeforeUtc, DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
        {
            AskedFor.Add(userId);
            return Task.FromResult(BriefingSection<BriefingNote>.Empty);
        }
    }
}
