using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Synap.Infrastructure.Services.Email;

/// <summary>Brevo's relay: STARTTLS on 587 with the SMTP key as password.</summary>
public sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly EmailSettings _settings;

    public MailKitSmtpTransport(IOptions<EmailSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = 20_000 };

        await client.ConnectAsync(
            _settings.SmtpServer,
            _settings.SmtpPort,
            _settings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);
        await client.AuthenticateAsync(_settings.SmtpUser, _settings.SmtpPass, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
