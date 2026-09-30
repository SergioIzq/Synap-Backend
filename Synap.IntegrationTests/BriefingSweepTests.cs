using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Briefing;
using Synap.Application.Features.Reminders;
using Synap.Domain;
using Synap.Infrastructure.BackgroundJobs;
using Synap.Infrastructure.Persistence;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Briefing;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// daily-briefing tasks 4.1 and 4.6 - the sweep and its hosted service against a real Postgres,
/// where the candidate query and its filtered index actually apply. Telegram is faked: nothing
/// leaves the process.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BriefingSweepTests
{
    private const string ChatId = "123456789";
    private const string Madrid = "Europe/Madrid";

    private readonly PostgresFixture _fixture;

    public BriefingSweepTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(User User, ServiceProvider Services, CapturingSender Telegram)> ArrangeAsync(
        bool telegramEnabled = true, int hour = 0, bool linkChat = true, bool enableBriefing = true)
    {
        await using var seed = _fixture.CreateContext();

        // The sweep is global: it considers every subscribed user in the database, including those
        // other tests in this collection left behind. PostgresCollection runs sequentially, so
        // turning the others off keeps each sweep to this test's user.
        await seed.Set<User>().Where(u => u.BriefingEnabled).ExecuteUpdateAsync(
            u => u.SetProperty(x => x.BriefingEnabled, false));

        var user = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        user.SetTimezone(Madrid);
        if (linkChat)
        {
            user.CompleteTelegramLink(ChatId);
        }

        if (enableBriefing)
        {
            // Hour 0 by default, so the sweep's own "now" is always past it.
            user.SetBriefing(enabled: true, hour);
        }

        seed.Add(user);
        // Something to report, so a briefing is worth sending.
        seed.Add(Note.Create(user.Id, NoteType.Text, "Sin etiquetar", "algo que apunté y no clasifiqué"));
        await seed.SaveChangesAsync();

        var telegram = new CapturingSender { Enabled = telegramEnabled };
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddDbContext<SynapDbContext>(options => options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserWriteRepository, UserWriteRepository>();
        services.AddScoped<IBriefingReadRepository>(_ => new BriefingReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString)));
        services.AddScoped<BriefingContentService>();
        services.AddScoped<BriefingDispatcher>();
        services.AddScoped<BriefingDeliveryService>();
        services.AddSingleton<WithheldBriefingRecorder>();
        services.AddSingleton<ITelegramSender>(telegram);
        services.AddSingleton<IPublisher>(new SilentPublisher());
        services.Configure<TelegramSettings>(o =>
        {
            o.Enabled = telegramEnabled;
            o.BotToken = "bot-token";
        });

        return (user, services.BuildServiceProvider(), telegram);
    }

    /// <summary>
    /// One sweep, run directly. The candidate query is what these tests are about, and going
    /// through the hosted service would make them wait on its timer - which, with the other
    /// collection's container running alongside, is a race rather than a check.
    /// </summary>
    private static async Task<int> SweepAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BriefingDeliveryService>().SweepAsync(DateTime.UtcNow);
    }

    /// <summary>The hosted service's first tick happens as soon as it starts.</summary>
    private static async Task RunOneTickAsync(ServiceProvider services, ILogger<BriefingSweepHostedService> logger)
    {
        var sweep = new BriefingSweepHostedService(
            services, services.GetRequiredService<IOptions<TelegramSettings>>(), logger);

        await sweep.StartAsync(default);
        await Task.Delay(500);
        await sweep.StopAsync(default);
    }

    // ---- 4.1 The candidate query ----

    [Fact]
    public async Task A_subscribed_user_past_their_hour_is_briefed()
    {
        var (user, services, telegram) = await ArrangeAsync();

        Assert.Equal(1, await SweepAsync(services));

        Assert.Equal(ChatId, Assert.Single(telegram.Snapshot()).ChatId);
        Assert.Contains("Sin etiquetar", telegram.Snapshot()[0].Text);

        await using var context = _fixture.CreateContext();
        var stored = await context.Set<User>().FirstAsync(u => u.Id == user.Id);
        Assert.NotNull(stored.BriefingLastResolvedOn);
    }

    [Fact]
    public async Task A_user_who_has_not_subscribed_is_never_considered()
    {
        var (_, services, telegram) = await ArrangeAsync(enableBriefing: false);

        await SweepAsync(services);

        Assert.Empty(telegram.Snapshot());
    }

    [Fact]
    public async Task A_user_whose_hour_has_not_come_is_left_alone()
    {
        // 23:00 local has not arrived for a sweep running now, whatever time it is - unless it is
        // the last hour of the day, which this guards against by using the current local hour + 2.
        var localHour = UserClock.ToLocal(DateTime.UtcNow, Madrid).Hour;
        var (user, services, telegram) = await ArrangeAsync(hour: (localHour + 2) % 24);
        if (localHour + 2 > 23)
        {
            return; // the "hour" would wrap past midnight and already be behind us
        }

        await SweepAsync(services);

        Assert.Empty(telegram.Snapshot());

        await using var context = _fixture.CreateContext();
        Assert.Null((await context.Set<User>().FirstAsync(u => u.Id == user.Id)).BriefingLastResolvedOn);
    }

    /// <summary>specs/briefing "Briefing on, Telegram not connected".</summary>
    [Fact]
    public async Task A_subscriber_with_no_linked_chat_is_not_briefed_and_the_day_stays_open()
    {
        var (user, services, telegram) = await ArrangeAsync(linkChat: false);

        await SweepAsync(services);

        Assert.Empty(telegram.Snapshot());

        await using var context = _fixture.CreateContext();
        Assert.Null((await context.Set<User>().FirstAsync(u => u.Id == user.Id)).BriefingLastResolvedOn);
    }

    // ---- 4.6 The hosted service ----

    /// <summary>specs/briefing "Delivery turned off for the deployment".</summary>
    [Fact]
    public async Task With_telegram_turned_off_the_sweep_does_not_run_at_all()
    {
        var (user, services, telegram) = await ArrangeAsync(telegramEnabled: false);
        var logger = new RecordingLogger();

        await RunOneTickAsync(services, logger);

        Assert.Empty(telegram.Snapshot());
        Assert.Contains(logger.Records, r => r.Contains("briefing sweep will not run"));

        await using var context = _fixture.CreateContext();
        Assert.Null((await context.Set<User>().FirstAsync(u => u.Id == user.Id)).BriefingLastResolvedOn);
    }

    /// <summary>A tick that throws must not end the sweep for the lifetime of the process.</summary>
    [Fact]
    public async Task A_tick_that_throws_is_recorded_and_the_service_stays_up()
    {
        var (_, services, _) = await ArrangeAsync();
        var broken = new ServiceCollection();
        broken.AddSingleton(NullLoggerFactory.Instance);
        broken.AddLogging();
        broken.Configure<TelegramSettings>(o => { o.Enabled = true; o.BotToken = "bot-token"; });
        // No BriefingDeliveryService registered: resolving it throws on every tick.
        var provider = broken.BuildServiceProvider();
        var logger = new RecordingLogger();

        var sweep = new BriefingSweepHostedService(provider, provider.GetRequiredService<IOptions<TelegramSettings>>(), logger);
        await sweep.StartAsync(default);
        await Task.Delay(500);
        var stillRunning = !sweep.ExecuteTask!.IsCompleted;
        await sweep.StopAsync(default);

        Assert.True(stillRunning, "a failing tick must not end the service");
        Assert.Contains(logger.Records, r => r.Contains("sweep tick failed"));

        await services.DisposeAsync();
    }

    /// <summary>The unit of work publishes domain events; nothing here listens for them.</summary>
    private sealed class SilentPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class CapturingSender : ITelegramSender
    {
        private readonly List<TelegramMessage> _sent = [];

        public bool Enabled { get; set; } = true;

        public bool IsEnabled => Enabled;

        public int Count
        {
            get { lock (_sent) { return _sent.Count; } }
        }

        public IReadOnlyList<TelegramMessage> Snapshot()
        {
            lock (_sent) { return _sent.ToList(); }
        }

        public Task<bool> SendAsync(TelegramMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sent) { _sent.Add(message); }

            return Task.FromResult(true);
        }

        public Task<bool> EditAsync(string chatId, long messageId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task AnswerCallbackAsync(string callbackId, string? toast = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger<BriefingSweepHostedService>
    {
        private readonly List<string> _records = [];

        public IReadOnlyList<string> Records
        {
            get { lock (_records) { return _records.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records) { _records.Add(formatter(state, exception)); }
        }
    }
}
