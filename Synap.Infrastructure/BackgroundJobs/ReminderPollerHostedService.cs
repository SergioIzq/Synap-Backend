using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Reminders;

namespace Synap.Infrastructure.BackgroundJobs;

/// <summary>
/// Sweeps the reminders table once a minute and delivers what has fallen due
/// (assistant-reminders design.md Decision 1). No Hangfire, no Quartz: the filtered index
/// idx_reminders_due_pending makes this a trivial query against the database we already run, and
/// ±1 minute is plenty for personal reminders.
///
/// Because the state lives in the table rather than in memory, a reminder that fell due while the
/// API was down is delivered on the first tick after it starts (specs/reminders "Due while the
/// system was down").
/// </summary>
public sealed class ReminderPollerHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Design.md Decision 4: a gap between sends, so a backlog doesn't trip Telegram's rate limit.</summary>
    public static readonly TimeSpan BetweenSends = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly TelegramSettings _telegram;
    private readonly ILogger<ReminderPollerHostedService> _logger;

    public ReminderPollerHostedService(
        IServiceProvider serviceProvider, IOptions<TelegramSettings> telegram, ILogger<ReminderPollerHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _telegram = telegram.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_telegram.IsConfigured)
        {
            // The Migration Plan's first deploy: the API runs with the poller off, and reminders
            // simply accumulate as pending (specs/reminders "Reminders are inert while delivery is
            // turned off").
            _logger.LogInformation("Reminder delivery is turned off; the poller will not run");
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A tick that throws - the database being briefly unreachable, say - must not end
                // the poller for the lifetime of the process.
                _logger.LogError(exception, "The reminder poller tick failed; retrying on the next one");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        // Its own scope per tick, like queued jobs: the DbContext and repositories are scoped.
        using var scope = _serviceProvider.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<ReminderDeliveryService>();

        var delivered = await delivery.DeliverDueAsync(
            DateTime.UtcNow,
            token => Task.Delay(BetweenSends, token),
            stoppingToken);

        if (delivered > 0)
        {
            _logger.LogInformation("Delivered {Count} reminder(s)", delivered);
        }
    }
}
