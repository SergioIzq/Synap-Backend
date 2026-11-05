using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.BackgroundJobs;

namespace Synap.Application.Features.Calendar.Commands;

/// <summary>
/// Cambia el ámbito, el color o si está activo de uno de los calendarios del usuario
/// (design.md Decisión 6). Desactivar borra los eventos copiados y resetea el estado de
/// sincronización para que al activarlo se rehaga (specs/calendar-integration "Calendario desactivado").
/// Un id ajeno devuelve NotFound.
/// </summary>
public sealed record PatchCalendarCommand(
    Guid CalendarId,
    string? Scope,
    string? Color,
    bool? Enabled)
    : ICommand;

public sealed class PatchCalendarCommandHandler : ICommandHandler<PatchCalendarCommand>
{
    private readonly ICalendarStore _store;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public PatchCalendarCommandHandler(ICalendarStore store, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _store = store;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(PatchCalendarCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();
        var calendar = await _store.GetForUpdateAsync(userId, request.CalendarId, cancellationToken);
        if (calendar is null)
        {
            return Result.Failure(Domain.Calendar.NotFound);
        }

        if (request.Scope is not null)
        {
            var scope = CalendarScopes.Parse(request.Scope);
            if (scope is null)
            {
                return Result.Failure(Domain.Calendar.InvalidScope);
            }

            calendar.ChangeScope(scope.Value);
        }

        if (request.Color is not null)
        {
            var colorResult = calendar.ChangeColor(request.Color);
            if (!colorResult.IsSuccess)
            {
                return colorResult;
            }
        }

        // Desactivar borra los eventos copiados (specs "Calendario desactivado").
        if (request.Enabled is false && calendar.Enabled)
        {
            calendar.Disable();
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await _store.DeleteEventsAsync(userId, request.CalendarId, cancellationToken);
            return Result.Success();
        }

        if (request.Enabled is true && !calendar.Enabled)
        {
            calendar.Enable();
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Encola una sincronización inmediata pedida por el usuario (design.md Decisión 6).
/// El rate-limiting lo gestiona el controlador antes de llamar a este comando.
/// </summary>
public sealed record EnqueueCalendarSyncCommand : ICommand;

public sealed class EnqueueCalendarSyncCommandHandler : ICommandHandler<EnqueueCalendarSyncCommand>
{
    private readonly IUserContext _userContext;
    private readonly IBackgroundJobQueue _jobs;

    public EnqueueCalendarSyncCommandHandler(IUserContext userContext, IBackgroundJobQueue jobs)
    {
        _userContext = userContext;
        _jobs = jobs;
    }

    public Task<Result> Handle(EnqueueCalendarSyncCommand request, CancellationToken cancellationToken)
    {
        CalendarSyncJobs.Enqueue(_jobs, _userContext.RequireUserId());
        return Task.FromResult(Result.Success());
    }
}
