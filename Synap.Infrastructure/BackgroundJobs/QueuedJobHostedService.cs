using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Synap.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs queued jobs one at a time in a fresh DI scope each - see design.md Decision 8. A job
/// failing (e.g. a bookmark scrape hitting a broken URL) is logged and does not affect the
/// queue or subsequent jobs.
/// </summary>
public sealed class QueuedJobHostedService : BackgroundService
{
    private readonly BackgroundJobQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<QueuedJobHostedService> _logger;

    public QueuedJobHostedService(BackgroundJobQueue queue, IServiceProvider serviceProvider, ILogger<QueuedJobHostedService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _serviceProvider.CreateScope();

            try
            {
                await job(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job failed");
            }
        }
    }
}
