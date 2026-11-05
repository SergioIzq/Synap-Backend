using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;

namespace Synap.Infrastructure.BackgroundJobs;

/// <summary>Los números de la sincronización periódica (calendar-store-and-view design.md Decisión 4).</summary>
public sealed class CalendarSyncSettings
{
    public const string SectionName = "CalendarSync";

    /// <summary>Apagada, solo se sincroniza al conectar, bajo demanda y antes del briefing.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Usuarios que se sincronizan a la vez.</summary>
    public int MaxParallelUsers { get; set; } = 4;

    /// <summary>
    /// Lo que puede tardar un usuario como mucho: un servidor colgado ocupa su hueco hasta
    /// entonces, y no más.
    /// </summary>
    public int PerUserTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Una pasada por todas las conexiones que no necesitan reconectarse, con como mucho
/// <see cref="CalendarSyncSettings.MaxParallelUsers"/> a la vez y cada una en su propio ámbito de
/// DI y con su propio plazo: un usuario cuyo servidor no responde no retrasa a los demás.
/// </summary>
public sealed class CalendarSyncSweep
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CalendarSyncSettings _settings;
    private readonly ILogger<CalendarSyncSweep> _logger;

    public CalendarSyncSweep(IServiceScopeFactory scopes, IOptions<CalendarSyncSettings> settings, ILogger<CalendarSyncSweep> logger)
    {
        _scopes = scopes;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        List<Guid> users;
        using (var scope = _scopes.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
            var ids = await context.Set<CalendarConnection>()
                .AsNoTracking()
                .Where(c => c.NeedsReconnectSince == null)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            users = ids.Select(id => id.Value).ToList();
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _settings.MaxParallelUsers),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(users, options, async (userId, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.PerUserTimeoutSeconds)));

            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICalendarSynchronizer>().SyncAsync(userId, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning("La sincronización del calendario de {UserId} superó su plazo", userId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Un usuario que falla no cuesta la pasada de los demás. Solo el tipo: el mensaje
                // podría llevar la dirección del servidor.
                _logger.LogError("La sincronización del calendario de {UserId} falló: {Type}", userId, exception.GetType().Name);
            }
        });
    }
}

/// <summary>Lanza <see cref="CalendarSyncSweep"/> cada <see cref="CalendarSyncSettings.IntervalMinutes"/> minutos.</summary>
public sealed class CalendarSyncHostedService : BackgroundService
{
    private readonly CalendarSyncSweep _sweep;
    private readonly CalendarSyncSettings _settings;
    private readonly ILogger<CalendarSyncHostedService> _logger;

    public CalendarSyncHostedService(
        CalendarSyncSweep sweep, IOptions<CalendarSyncSettings> settings, ILogger<CalendarSyncHostedService> logger)
    {
        _sweep = sweep;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("La sincronización periódica del calendario está apagada");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _settings.IntervalMinutes)));

        // La primera pasada sale ya: tras un despliegue es la que apunta las conexiones antiguas a
        // su home y hace la copia completa (design.md, Migration Plan).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _sweep.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Una pasada que lanza no acaba con la sincronización mientras viva el proceso.
                _logger.LogError(exception, "La pasada de sincronización del calendario falló; se reintenta en la siguiente");
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
}
