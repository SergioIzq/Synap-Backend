using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Reminders.Commands;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Reminders;

/// <summary>assistant-reminders tasks 4.2 to 4.4 - specs/reminders "Connecting a Telegram account".</summary>
public class TelegramLinkTests
{
    private const string ChatId = "123456789";

    private readonly FakeUserRepository _users = new();
    private readonly FakeUserSettingsView _reads = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private static readonly IOptions<TelegramSettings> Telegram =
        Options.Create(new TelegramSettings { Enabled = true, BotToken = "token", BotUsername = "SynapBot" });

    private User SeedUser()
    {
        var user = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        _users.Add(user);
        _reads.Users.Add(user);
        return user;
    }

    private StartTelegramLinkCommandHandler Start(User user)
        => new(_users, _unitOfWork, new FakeUserContext(user.Id.Value), Telegram);

    private CompleteTelegramLinkCommandHandler Complete() => new(_reads, _users, _unitOfWork);

    // ---- Issuing a code (task 4.2) ----

    [Fact]
    public async Task A_code_is_issued_with_the_bot_username_and_a_fifteen_minute_life()
    {
        var user = SeedUser();
        var before = DateTime.UtcNow;

        var result = await Start(user).Handle(new StartTelegramLinkCommand(), default);

        // The DbContext defaults to NoTracking outside Development, so a user write that skips
        // Update() persists nothing at all - as an integration test caught the hard way.
        Assert.Equal(1, _users.UpdateCalls);
        Assert.True(result.IsSuccess);
        Assert.Equal(64, result.Value.Code.Length);
        Assert.Equal("SynapBot", result.Value.BotUsername);
        Assert.InRange(result.Value.ExpiresAtUtc, before.AddMinutes(14), DateTime.UtcNow.AddMinutes(15));
        Assert.Equal(result.Value.Code, user.TelegramLinkToken);
    }

    [Fact]
    public async Task Two_requests_give_different_codes_and_only_the_newer_one_works()
    {
        var user = SeedUser();

        var first = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value;
        var second = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value;

        Assert.NotEqual(first.Code, second.Code);
        Assert.Equal(second.Code, user.TelegramLinkToken);
        Assert.False((await Complete().Handle(new CompleteTelegramLinkCommand(first.Code, ChatId), default)).Value);
        Assert.True((await Complete().Handle(new CompleteTelegramLinkCommand(second.Code, ChatId), default)).Value);
    }

    [Fact]
    public async Task The_status_never_gives_the_code_back()
    {
        var user = SeedUser();
        await Start(user).Handle(new StartTelegramLinkCommand(), default);

        var status = await new GetTelegramStatusQueryHandler(_reads, new FakeUserContext(user.Id.Value))
            .Handle(new GetTelegramStatusQuery(), default);

        // Only the connection state and the timezone are readable; the code is write-only.
        Assert.False(status.Value.Connected);
        Assert.Null(status.Value.Timezone);
    }

    // ---- Completing the link (task 4.3) ----

    [Fact]
    public async Task A_valid_code_links_the_chat_and_clears_the_code()
    {
        var user = SeedUser();
        var code = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value.Code;

        var linked = await Complete().Handle(new CompleteTelegramLinkCommand(code, ChatId), default);

        Assert.True(linked.Value);
        Assert.Equal(ChatId, user.TelegramChatId);
        Assert.Null(user.TelegramLinkToken);
    }

    [Fact]
    public async Task An_unknown_code_links_nothing()
    {
        SeedUser();

        var linked = await Complete().Handle(new CompleteTelegramLinkCommand("deadbeef", ChatId), default);

        Assert.False(linked.Value);
        Assert.All(_reads.Users, u => Assert.False(u.HasTelegram));
    }

    [Fact]
    public async Task An_expired_code_links_nothing()
    {
        var user = SeedUser();
        user.StartTelegramLink("expired-code", DateTime.UtcNow.AddSeconds(-1));

        var linked = await Complete().Handle(new CompleteTelegramLinkCommand("expired-code", ChatId), default);

        Assert.False(linked.Value);
        Assert.False(user.HasTelegram);
    }

    [Fact]
    public async Task An_already_used_code_links_nothing_the_second_time()
    {
        var user = SeedUser();
        var code = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value.Code;
        await Complete().Handle(new CompleteTelegramLinkCommand(code, ChatId), default);

        // Even from another chat: the code is gone.
        var again = await Complete().Handle(new CompleteTelegramLinkCommand(code, "999999999"), default);

        Assert.False(again.Value);
        Assert.Equal(ChatId, user.TelegramChatId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_code_links_nothing(string? code)
        => Assert.False((await Complete().Handle(new CompleteTelegramLinkCommand(code, ChatId), default)).Value);

    // ---- Disconnecting (task 4.4) ----

    [Fact]
    public async Task Disconnecting_clears_the_chat_and_any_outstanding_code()
    {
        var user = SeedUser();
        var code = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value.Code;
        await Complete().Handle(new CompleteTelegramLinkCommand(code, ChatId), default);
        await Start(user).Handle(new StartTelegramLinkCommand(), default);

        var result = await new DisconnectTelegramCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new DisconnectTelegramCommand(), default);

        Assert.True(result.IsSuccess);
        Assert.False(user.HasTelegram);
        Assert.Null(user.TelegramLinkToken);
        Assert.True(_users.UpdateCalls > 0);
    }

    [Fact]
    public async Task Connecting_again_needs_a_fresh_code_and_the_old_one_is_dead()
    {
        var user = SeedUser();
        var oldCode = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value.Code;
        await Complete().Handle(new CompleteTelegramLinkCommand(oldCode, ChatId), default);
        await new DisconnectTelegramCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new DisconnectTelegramCommand(), default);

        Assert.False((await Complete().Handle(new CompleteTelegramLinkCommand(oldCode, ChatId), default)).Value);

        var newCode = (await Start(user).Handle(new StartTelegramLinkCommand(), default)).Value.Code;
        Assert.True((await Complete().Handle(new CompleteTelegramLinkCommand(newCode, ChatId), default)).Value);
        Assert.True(user.HasTelegram);
    }

    [Fact]
    public async Task Disconnecting_leaves_the_users_reminders_pending()
    {
        var user = SeedUser();
        var reminders = new FakeReminderRepository();
        var pending = reminders.Add(Reminder.Create(user.Id, "Sigue pendiente", DateTime.UtcNow.AddDays(1), DateTime.UtcNow).Value);

        await new DisconnectTelegramCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new DisconnectTelegramCommand(), default);

        Assert.True(pending.IsPending);
        Assert.Single(await reminders.ListPendingByUserAsync(user.Id.Value));
    }
}
