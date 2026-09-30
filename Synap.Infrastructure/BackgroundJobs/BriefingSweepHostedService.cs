using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Briefing;
using Synap.Application.Features.Reminders;

namespace Synap.Infrastructure.BackgroundJobs;

/// <summary>
/// Sweeps for the morning briefings that are owed (daily-briefing design.md Decision 2). Its own
/// job rather than one more responsibility on the reminder poller: a briefing that throws must not
/// stop a reminder from being delivered, and the two do not run at the same cadence.
///
/// Every quarter of an hour is enough. The promise is "at or after the hour the user chose", never
/// "on the stroke of it" - the sweep's own interval is what makes any finer promise a lie.
/// </summary>
public sealed class BriefingSweepHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _serviceProvider;
    private readonly TelegramSettings _telegram;
    private readonly ILogger<BriefingSweepHostedService> _logger;

    public BriefingSweepHostedService(
        IServiceProvider serviceProvider, IOptions<TelegramSettings> telegram, ILogger<BriefingSweepHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _telegram = telegram.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_telegram.IsConfigured)
        {
            // Telegram is the only channel, so with delivery off there is nothing this could do
            // (specs/briefing "Delivery turned off for the deployment").
            _logger.LogInformation("Telegram delivery is turned off; the briefing sweep will not run");
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A tick that throws must not end the sweep for the lifetime of the process.
                _logger.LogError(exception, "The briefing sweep tick failed; retrying on the next one");
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

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        // A scope per tick: the delivery service uses the DbContext.
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<BriefingDeliveryService>();

        var sent = await service.SweepAsync(DateTime.UtcNow, stoppingToken);
        if (sent > 0)
        {
            _logger.LogInformation("Briefings sent: {Count}", sent);
        }
    }
}
