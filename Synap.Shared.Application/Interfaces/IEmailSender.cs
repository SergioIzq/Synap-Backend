namespace Synap.Shared.Application.Interfaces;

/// <summary>A transactional email; TextBody is the plain-text alternative for clients without HTML.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Sends transactional email in the background (password-recovery design.md Decision 3):
/// Enqueue returns immediately, so a request never waits on - or reveals timing of - the SMTP
/// provider. Failures are logged; there is no persistent retry.
/// </summary>
public interface IEmailSender
{
    void Enqueue(EmailMessage message);
}
