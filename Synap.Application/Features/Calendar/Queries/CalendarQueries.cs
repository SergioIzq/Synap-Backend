using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Calendar.Queries;

// ---- Respuestas ----

/// <summary>Resumen de un calendario copiado (design.md Decisión 6): preferencias y estado de sincronización.</summary>
public sealed record CalendarSummary(
    Guid Id,
    string Name,
    string EffectiveColor,
    string? Color,
    string Scope,
    bool Enabled,
    DateTime? LastSyncedAt,
    string? LastSyncError);

public sealed record CalendarListResponse(IReadOnlyList<CalendarSummary> Calendars);

/// <summary>Un evento en la respuesta de la API (design.md Decisión 6).</summary>
public sealed record CalendarEventDto(
    string Title,
    DateTime StartUtc,
    DateTime? EndUtc,
    bool IsAllDay,
    string? AllDayDate,
    bool IsRecurring,
    string? Location,
    string? Description,
    string? CalendarId,
    string Scope,
    string Color);

public sealed record CalendarEventsResponse(
    IReadOnlyList<CalendarEventDto> Events,
    DateTime? LastSyncedAt,
    bool NeedsReconnect,
    bool SyncFailing);

// ---- GET /api/calendar/calendars ----

public sealed record GetCalendarsQuery : IQuery<CalendarListResponse>;

public sealed class GetCalendarsQueryHandler : IQueryHandler<GetCalendarsQuery, CalendarListResponse>
{
    private readonly ICalendarStore _store;
    private readonly IUserContext _userContext;

    public GetCalendarsQueryHandler(ICalendarStore store, IUserContext userContext)
    {
        _store = store;
        _userContext = userContext;
    }

    public async Task<Result<CalendarListResponse>> Handle(
        GetCalendarsQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();
        var calendars = await _store.ListAsync(userId, cancellationToken);

        var items = calendars.Select(c => new CalendarSummary(
            c.Id.Value,
            c.DisplayName,
            c.EffectiveColor,
            c.Color,
            CalendarScopes.ToText(c.Scope),
            c.Enabled,
            c.LastSyncedAt,
            c.LastSyncError))
            .ToList();

        return Result.Success(new CalendarListResponse(items));
    }
}

// ---- GET /api/calendar/events ----

public sealed record GetCalendarEventsQuery(DateTime FromUtc, DateTime ToUtc, string? Scope)
    : IQuery<CalendarEventsResponse>
{
    public const int MaxDays = 62;
    public const int MaxEvents = 500;
}

public sealed class GetCalendarEventsQueryHandler : IQueryHandler<GetCalendarEventsQuery, CalendarEventsResponse>
{
    private readonly ICalendarReader _reader;
    private readonly IUserContext _userContext;

    public static readonly Error IntervalTooLong =
        Error.Validation($"El intervalo de fechas no puede superar los {GetCalendarEventsQuery.MaxDays} días.");

    public static readonly Error InvalidInterval =
        Error.Validation("El inicio tiene que ser anterior al fin.");

    public GetCalendarEventsQueryHandler(ICalendarReader reader, IUserContext userContext)
    {
        _reader = reader;
        _userContext = userContext;
    }

    public async Task<Result<CalendarEventsResponse>> Handle(
        GetCalendarEventsQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        if (request.FromUtc >= request.ToUtc)
        {
            return Result.Failure<CalendarEventsResponse>(InvalidInterval);
        }

        if ((request.ToUtc - request.FromUtc).TotalDays > GetCalendarEventsQuery.MaxDays)
        {
            return Result.Failure<CalendarEventsResponse>(IntervalTooLong);
        }

        var scope = CalendarScopes.Parse(request.Scope);
        if (request.Scope is not null && scope is null)
        {
            return Result.Failure<CalendarEventsResponse>(Domain.Calendar.InvalidScope);
        }

        var read = await _reader.ListEventsAsync(
            userId, request.FromUtc, request.ToUtc, GetCalendarEventsQuery.MaxEvents, scope, cancellationToken);

        return read switch
        {
            CalendarReadResult.NotConnected =>
                Result.Success(new CalendarEventsResponse([], null, false, false)),
            CalendarReadResult.Ok(var events, var state) =>
                Result.Success(new CalendarEventsResponse(
                    events.Select(ToDto).ToList(),
                    state.LastSyncedAt,
                    state.NeedsReconnect,
                    state.SyncFailing)),
            _ => Result.Success(new CalendarEventsResponse([], null, false, false)),
        };
    }

    private static CalendarEventDto ToDto(CalendarEvent e) => new(
        e.Title,
        e.StartUtc,
        e.IsAllDay ? null : e.EndUtc,
        e.IsAllDay,
        e.AllDayDate?.ToString("yyyy-MM-dd"),
        e.IsRecurring,
        e.Location,
        e.Description,
        e.Calendar?.CalendarId.ToString(),
        CalendarScopes.ToText(e.Scope),
        e.Calendar?.Color ?? "#3b82f6");
}
