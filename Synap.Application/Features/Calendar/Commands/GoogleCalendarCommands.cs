using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Calendar.Commands;

/// <summary>
/// Paso 1 de la conexión con Google (calendar-integration design.md Decisión 3): guarda un nonce
/// nuevo en el usuario, que sustituye cualquier inicio anterior, y devuelve la URL de consentimiento.
/// </summary>
public sealed record StartGoogleCalendarCommand : ICommand<GoogleCalendarStartResponse>;

public sealed record GoogleCalendarStartResponse(string AuthorizationUrl);

public sealed class StartGoogleCalendarCommandHandler
    : ICommandHandler<StartGoogleCalendarCommand, GoogleCalendarStartResponse>
{
    private readonly IUserWriteRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IGoogleCalendarAuthorization _google;
    private readonly GoogleOAuthState _state;
    private readonly GoogleCalendarOptions _options;

    public StartGoogleCalendarCommandHandler(
        IUserWriteRepository users,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IGoogleCalendarAuthorization google,
        GoogleOAuthState state,
        IOptions<GoogleCalendarOptions> options)
    {
        _users = users;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _google = google;
        _state = state;
        _options = options.Value;
    }

    public async Task<Result<GoogleCalendarStartResponse>> Handle(
        StartGoogleCalendarCommand request, CancellationToken cancellationToken)
    {
        if (!_options.IsAvailable)
        {
            return Result.Failure<GoogleCalendarStartResponse>(CalendarErrors.GoogleNotAvailable);
        }

        var user = await _users.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<GoogleCalendarStartResponse>(CalendarErrors.UserNotFound);
        }

        var nonce = GoogleOAuthState.NewNonce();
        var expiresAt = DateTime.UtcNow + GoogleOAuthState.Ttl;
        user.StartCalendarOAuth(nonce, expiresAt);
        _users.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new GoogleCalendarStartResponse(
            _google.BuildConsentUrl(_state.Issue(user.Id.Value, nonce, expiresAt))));
    }
}

/// <summary>Cómo terminó la vuelta de Google; es lo que la web recibe en `?calendar=`.</summary>
public enum GoogleCalendarOutcome
{
    Connected,
    Denied,
    Error,
}

/// <summary>
/// Paso 2: la vuelta de Google, anónima porque la redirección del navegador no lleva el JWT. Solo
/// guarda algo si el `state` es de un inicio vigente de ese usuario y el nonce no se ha usado; en
/// cualquier otro caso no toca la conexión que hubiera (specs/calendar-integration "Respuesta de
/// Google ajena o caducada", "Permiso denegado").
/// </summary>
public sealed record CompleteGoogleCalendarCommand(string? Code, string? State, string? Error)
    : ICommand<GoogleCalendarOutcome>;

public sealed class CompleteGoogleCalendarCommandHandler
    : ICommandHandler<CompleteGoogleCalendarCommand, GoogleCalendarOutcome>
{
    private readonly IUserWriteRepository _users;
    private readonly ICalendarConnectionWriteRepository _connections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGoogleCalendarAuthorization _google;
    private readonly GoogleOAuthState _state;
    private readonly CalendarCredentialsProtector _protector;
    private readonly GoogleCalendarOptions _options;
    private readonly ILogger<CompleteGoogleCalendarCommandHandler> _logger;

    public CompleteGoogleCalendarCommandHandler(
        IUserWriteRepository users,
        ICalendarConnectionWriteRepository connections,
        IUnitOfWork unitOfWork,
        IGoogleCalendarAuthorization google,
        GoogleOAuthState state,
        CalendarCredentialsProtector protector,
        IOptions<GoogleCalendarOptions> options,
        ILogger<CompleteGoogleCalendarCommandHandler> logger)
    {
        _users = users;
        _connections = connections;
        _unitOfWork = unitOfWork;
        _google = google;
        _state = state;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<GoogleCalendarOutcome>> Handle(
        CompleteGoogleCalendarCommand request, CancellationToken cancellationToken)
    {
        return Result.Success(await CompleteAsync(request, cancellationToken));
    }

    private async Task<GoogleCalendarOutcome> CompleteAsync(
        CompleteGoogleCalendarCommand request, CancellationToken cancellationToken)
    {
        if (!_options.IsAvailable)
        {
            return GoogleCalendarOutcome.Error;
        }

        var now = DateTime.UtcNow;
        if (_state.TryRead(request.State) is not { } state || state.ExpiresAt < now)
        {
            _logger.LogWarning("Vuelta de Google con un state ilegible o caducado");
            return GoogleCalendarOutcome.Error;
        }

        var user = await _users.GetByIdAsync(state.UserId, cancellationToken);
        if (user is null)
        {
            return GoogleCalendarOutcome.Error;
        }

        // El nonce se consume pase lo que pase: una respuesta capturada no se puede repetir.
        var nonceMatched = user.ConsumeCalendarOAuth(state.Nonce, now);
        _users.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (!nonceMatched)
        {
            _logger.LogWarning("Vuelta de Google para {UserId} con un nonce usado o ajeno", state.UserId);
            return GoogleCalendarOutcome.Error;
        }

        if (!string.IsNullOrEmpty(request.Error) || string.IsNullOrWhiteSpace(request.Code))
        {
            _logger.LogInformation("El usuario {UserId} no concedió el acceso a Google Calendar", state.UserId);
            return GoogleCalendarOutcome.Denied;
        }

        var grant = await _google.ExchangeCodeAsync(request.Code, cancellationToken);
        if (grant is null)
        {
            _logger.LogWarning("Google no aceptó el código de {UserId}", state.UserId);
            return GoogleCalendarOutcome.Error;
        }

        var account = await _google.GetAccountAsync(grant.AccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(account))
        {
            _logger.LogWarning("No se pudo leer la cuenta de Google de {UserId}", state.UserId);
            return GoogleCalendarOutcome.Error;
        }

        var credentials = _protector.Protect(
            new CalendarCredentials.Google(grant.RefreshToken, grant.AccessToken, grant.AccessTokenExpiresAt));

        await CalendarConnections.ReplaceAsync(
            _connections, state.UserId, CalendarProvider.Google, account, "primary", credentials, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return GoogleCalendarOutcome.Connected;
    }
}
