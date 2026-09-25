using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Net;

namespace Synap.Application.Features.Users.Commands.ForgotPassword;

/// <summary>
/// specs/identity "Password recovery by email". Always succeeds - unknown addresses, malformed
/// input and the per-address limit all look identical to the caller, so nobody can probe which
/// emails have an account (password-recovery design.md Decision 2).
/// </summary>
public sealed record ForgotPasswordCommand(string Email) : ICommand;

public sealed class ForgotPasswordCommandHandler : ICommandHandler<ForgotPasswordCommand>
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    private readonly IUserReadRepository _userReadRepository;
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApiTokenHasher _tokenHasher;
    private readonly IEmailSender _emailSender;
    private readonly IRecoveryRequestLimiter _limiter;
    private readonly AppOptions _appOptions;

    public ForgotPasswordCommandHandler(
        IUserReadRepository userReadRepository,
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IApiTokenHasher tokenHasher,
        IEmailSender emailSender,
        IRecoveryRequestLimiter limiter,
        IOptions<AppOptions> appOptions)
    {
        _userReadRepository = userReadRepository;
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _tokenHasher = tokenHasher;
        _emailSender = emailSender;
        _limiter = limiter;
        _appOptions = appOptions.Value;
    }

    public async Task<Result> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        var email = Email.Create(request.Email ?? string.Empty);
        if (email.IsFailure)
        {
            return Result.Success();
        }

        var found = await _userReadRepository.GetByEmailAsync(email.Value, cancellationToken);
        if (found is null || !_limiter.TryAcquire(email.Value.Value))
        {
            return Result.Success();
        }

        var user = await _userWriteRepository.GetByIdAsync(found.Id.Value, cancellationToken);
        if (user is null)
        {
            return Result.Success();
        }

        // Same opaque token format as the personal access token; only its SHA-256 is stored.
        var (token, tokenHash) = _tokenHasher.GenerateToken();
        user.StartPasswordReset(tokenHash, DateTime.UtcNow.Add(LinkLifetime));
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var link = $"{_appOptions.PublicBaseUrl.TrimEnd('/')}/auth/reset-password?token={Uri.EscapeDataString(token)}";
        _emailSender.Enqueue(PasswordResetEmail.Build(user.Email.Value, link));

        return Result.Success();
    }
}

/// <summary>The recovery email, in Spanish, as HTML with a plain-text alternative.</summary>
public static class PasswordResetEmail
{
    public const string Subject = "Restablece tu contraseña de Synap";

    public static EmailMessage Build(string to, string link)
    {
        var safeLink = WebUtility.HtmlEncode(link);

        var html = $"""
            <!doctype html>
            <html lang="es">
              <body style="margin:0;padding:24px;background:#f8fafc;font-family:Arial,Helvetica,sans-serif;color:#334155;">
                <table role="presentation" width="100%" style="max-width:480px;margin:0 auto;background:#ffffff;border-radius:12px;border:1px solid #e2e8f0;">
                  <tr><td style="padding:28px 28px 8px;">
                    <h1 style="margin:0 0 12px;font-size:20px;color:#1e1b4b;">Restablecer tu contraseña</h1>
                    <p style="margin:0 0 16px;line-height:1.5;">Hemos recibido una solicitud para restablecer la contraseña de tu cuenta de Synap.</p>
                    <p style="margin:0 0 24px;">
                      <a href="{safeLink}" style="display:inline-block;padding:12px 20px;background:#4f46e5;color:#ffffff;text-decoration:none;border-radius:8px;font-weight:bold;">Elegir una contraseña nueva</a>
                    </p>
                    <p style="margin:0 0 8px;font-size:13px;line-height:1.5;color:#64748b;">El enlace caduca en 1 hora y solo se puede usar una vez. Al cambiarla se cerrará la sesión en todos tus dispositivos.</p>
                    <p style="margin:0 0 20px;font-size:13px;line-height:1.5;color:#64748b;">Si no has sido tú, ignora este correo: tu contraseña no cambiará.</p>
                  </td></tr>
                  <tr><td style="padding:12px 28px 24px;font-size:12px;color:#94a3b8;border-top:1px solid #e2e8f0;">Synap · tu segundo cerebro</td></tr>
                </table>
              </body>
            </html>
            """;

        var text = $"""
            Restablecer tu contraseña de Synap

            Hemos recibido una solicitud para restablecer la contraseña de tu cuenta.
            Abre este enlace para elegir una nueva (caduca en 1 hora y solo se puede usar una vez):

            {link}

            Al cambiarla se cerrará la sesión en todos tus dispositivos.
            Si no has sido tú, ignora este correo: tu contraseña no cambiará.
            """;

        return new EmailMessage(to, Subject, html, text);
    }
}
