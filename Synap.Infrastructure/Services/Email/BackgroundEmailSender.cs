using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Email;

/// <summary>
/// Queues each email on the in-process BackgroundJobQueue and delivers it through
/// ISmtpTransport (password-recovery design.md Decision 3) - instead of the kernel's
/// AddKernelEmail, which would drag in Hangfire and a kernel version bump. Without SMTP
/// credentials (e.g. local dev) nothing is sent and each email is logged as skipped, so the
/// app still runs.
/// </summary>
public sealed class BackgroundEmailSender : IEmailSender
{
    private readonly IBackgroundJobQueue _queue;
    private readonly EmailSettings _settings;
    private readonly ILogger<BackgroundEmailSender> _logger;

    public BackgroundEmailSender(IBackgroundJobQueue queue, IOptions<EmailSettings> settings, ILogger<BackgroundEmailSender> logger)
    {
        _queue = queue;
        _settings = settings.Value;
        _logger = logger;
    }

    public void Enqueue(EmailMessage message)
    {
        if (!_settings.HasCredentials)
        {
            _logger.LogWarning("Email not sent to {To} ({Subject}): SMTP is not configured (EmailSettings:SmtpUser/SmtpPass).", Mask(message.To), message.Subject);
            return;
        }

        var mime = BuildMime(message);

        _queue.Enqueue(async (services, cancellationToken) =>
        {
            var transport = services.GetRequiredService<ISmtpTransport>();
            var logger = services.GetRequiredService<ILogger<BackgroundEmailSender>>();
            try
            {
                await transport.SendAsync(mime, cancellationToken);
                logger.LogInformation("Email sent to {To} ({Subject})", Mask(message.To), message.Subject);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Email to {To} ({Subject}) could not be sent", Mask(message.To), message.Subject);
            }
        });
    }

    private MimeMessage BuildMime(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();
        return mime;
    }

    // Logs never carry full addresses: "se***@gmail.com".
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 2 ? "***" + email[Math.Max(at, 0)..] : email[..2] + "***" + email[at..];
    }
}
