using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Users;
using Synap.Domain;
using Synap.Infrastructure.BackgroundJobs;
using Synap.Infrastructure.Persistence;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Reminders;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// assistant-reminders task 5.5 - the poller against a real Postgres, so the filtered-index query
/// and the "delivered once, never twice" rule are exercised for real. Telegram itself is faked:
/// nothing leaves the process.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReminderPollerTests
{
    private const string ChatId = "123456789";

    private readonly PostgresFixture _fixture;

    public ReminderPollerTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(User User, ServiceProvider Services, CapturingTelegramSender Telegram)> ArrangeAsync(bool telegramEnabled = true)
    {
        await using var seed = _fixture.CreateContext();

        // The poller is global: it delivers every due reminder in the database, including those
        // other tests in this collection left behind. Clearing them keeps each tick to one message,
        // which also means no 5-second gap to wait out. Safe because PostgresCollection runs
        // sequentially.
        await seed.Set<Reminder>().ExecuteDeleteAsync();

        var user = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        user.SetTimezone("Europe/Madrid");
        user.CompleteTelegramLink(ChatId);
        seed.Add(user);
        await seed.SaveChangesAsync();

        var telegram = new CapturingTelegramSender { Enabled = telegramEnabled };
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddDbContext<SynapDbContext>(options => options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IReminderWriteRepository, ReminderWriteRepository>();
        services.AddSingleton<ITelegramSender>(telegram);
        services.AddSingleton<IPublisher>(new NoOpPublisher());
        services.AddScoped<ReminderDeliveryService>();
        services.AddSingleton<WithheldReminderRecorder>();
        services.Configure<AppOptions>(o => o.PublicBaseUrl = "https://synap.test");
        services.Configure<TelegramSettings>(o =>
        {
            o.Enabled = telegramEnabled;
            o.BotToken = "bot-token";
        });

        return (user, services.BuildServiceProvider(), telegram);
    }

    private async Task<Reminder> SeedReminderAsync(User user, string text, DateTime dueAt, string? recurrence = null)
    {
        await using var context = _fixture.CreateContext();
        var reminder = Reminder.Create(user.Id, text, dueAt, dueAt.AddMinutes(-1), recurrence: recurrence).Value;
        context.Add(reminder);
        await context.SaveChangesAsync();
        return reminder;
    }

    /// <summary>
    /// The poller's first tick happens as soon as it starts, before it waits for the next one.
    /// <paramref name="until"/> is polled so a passing test doesn't sit on a fixed delay; without it
    /// the tick is simply given a moment (the "nothing should be delivered" cases).
    /// </summary>
    private static async Task RunOneTickAsync(ServiceProvider services, Func<bool>? until = null)
    {
        var poller = new ReminderPollerHostedService(
            services,
            services.GetRequiredService<IOptions<TelegramSettings>>(),
            NullLogger<ReminderPollerHostedService>.Instance);

        await poller.StartAsync(default);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(until?.Invoke() ?? false))
        {
            await Task.Delay(100);
        }

        if (until is null)
        {
            await Task.Delay(1000);
        }

        await poller.StopAsync(default);
    }

    [Fact]
    public async Task A_reminder_that_is_already_due_is_delivered_on_the_next_tick()
    {
        var (user, services, telegram) = await ArrangeAsync();
        await using var _ = services;
        var reminder = await SeedReminderAsync(user, "Renovar el certificado SSL", DateTime.UtcNow.AddMinutes(-1));

        await RunOneTickAsync(services, () => telegram.Sent.Count > 0);

        var sent = Assert.Single(telegram.Sent, m => m.Text.Contains("Renovar el certificado SSL"));
        Assert.Equal(ChatId, sent.ChatId);

        await using var context = _fixture.CreateContext();
        var stored = await context.Set<Reminder>().AsNoTracking().FirstAsync(r => r.Id == reminder.Id);
        Assert.NotNull(stored.SentAt);
        Assert.False(stored.IsPending);
    }

    [Fact]
    public async Task A_reminder_due_in_the_future_is_not_delivered()
    {
        var (user, services, telegram) = await ArrangeAsync();
        await using var _ = services;
        var reminder = await SeedReminderAsync(user, "Todavía no toca", DateTime.UtcNow.AddDays(30));

        await RunOneTickAsync(services);

        Assert.DoesNotContain(telegram.Sent, m => m.Text.Contains("Todavía no toca"));

        await using var context = _fixture.CreateContext();
        var stored = await context.Set<Reminder>().AsNoTracking().FirstAsync(r => r.Id == reminder.Id);
        Assert.Null(stored.SentAt);
        Assert.True(stored.IsPending);
    }

    [Fact]
    public async Task A_delivered_reminder_is_not_delivered_again_on_a_later_tick()
    {
        var (user, services, telegram) = await ArrangeAsync();
        await using var _ = services;
        await SeedReminderAsync(user, "Solo una vez", DateTime.UtcNow.AddMinutes(-1));

        await RunOneTickAsync(services, () => telegram.Sent.Count > 0);
        await RunOneTickAsync(services);

        Assert.Single(telegram.Sent, m => m.Text.Contains("Solo una vez"));
    }

    [Fact]
    public async Task The_poller_does_not_run_at_all_while_delivery_is_turned_off()
    {
        var (user, services, telegram) = await ArrangeAsync(telegramEnabled: false);
        await using var _ = services;
        var reminder = await SeedReminderAsync(user, "Inerte", DateTime.UtcNow.AddMinutes(-1));

        await RunOneTickAsync(services);

        Assert.Empty(telegram.Sent);

        await using var context = _fixture.CreateContext();
        var stored = await context.Set<Reminder>().AsNoTracking().FirstAsync(r => r.Id == reminder.Id);
        Assert.True(stored.IsPending);
    }

    [Fact]
    public async Task A_reminder_that_fell_due_while_the_system_was_down_is_delivered_when_it_starts()
    {
        var (user, services, telegram) = await ArrangeAsync();
        await using var _ = services;
        // Two days late: lateness must not disqualify it.
        await SeedReminderAsync(user, "Con mucho retraso", DateTime.UtcNow.AddDays(-2));

        await RunOneTickAsync(services, () => telegram.Sent.Count > 0);

        Assert.Single(telegram.Sent, m => m.Text.Contains("Con mucho retraso"));
    }

    private sealed class CapturingTelegramSender : ITelegramSender
    {
        public bool Enabled { get; set; } = true;

        public List<TelegramMessage> Sent { get; } = [];

        public bool IsEnabled => Enabled;

        public Task<bool> SendAsync(TelegramMessage message, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(message);
            }

            return Task.FromResult(true);
        }

        public Task<bool> EditAsync(string chatId, long messageId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task AnswerCallbackAsync(string callbackId, string? toast = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>The UnitOfWork dispatches domain events; reminders raise none.</summary>
    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
