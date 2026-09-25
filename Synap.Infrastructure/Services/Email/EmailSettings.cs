namespace Synap.Infrastructure.Services.Email;

/// <summary>
/// Bound from "EmailSettings" - same keys as Kash-Backend's, so its Brevo configuration can be
/// copied as-is. SmtpUser/SmtpPass are never committed: user-secrets locally, EMAIL_SMTP_USER /
/// EMAIL_SMTP_PASS in the .env on Docker/VPS.
/// </summary>
public sealed class EmailSettings
{
    public const string SectionName = "EmailSettings";

    public string SmtpServer { get; set; } = "smtp-relay.brevo.com";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string FromEmail { get; set; } = "no-reply@sergioizq.com";
    public string FromName { get; set; } = "Synap";
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPass { get; set; } = string.Empty;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(SmtpUser) && !string.IsNullOrWhiteSpace(SmtpPass);
}
