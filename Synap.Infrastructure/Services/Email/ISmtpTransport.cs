using MimeKit;

namespace Synap.Infrastructure.Services.Email;

/// <summary>The actual SMTP delivery - separate from BackgroundEmailSender so tests can replace it.</summary>
public interface ISmtpTransport
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);
}
