using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Synap.Infrastructure.Services.Email;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Application.Interfaces;

namespace Synap.UnitTests.Services.Email;

/// <summary>password-recovery task 3.1.</summary>
public class BackgroundEmailSenderTests
{
    private sealed class InlineQueue(IServiceProvider services) : IBackgroundJobQueue
    {
        public int Enqueued { get; private set; }

        public void Enqueue(Func<IServiceProvider, CancellationToken, Task> job)
        {
            Enqueued++;
            job(services, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private sealed class RecordingTransport(Exception? failWith = null) : ISmtpTransport
    {
        public List<MimeMessage> Sent { get; } = [];

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            if (failWith is not null) throw failWith;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static readonly EmailMessage Message = new("sergio@example.com", "Asunto", "<p>Hola</p>", "Hola");

    private static (BackgroundEmailSender Sender, InlineQueue Queue) Create(EmailSettings settings, RecordingTransport transport)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISmtpTransport>(transport)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var queue = new InlineQueue(services);
        return (new BackgroundEmailSender(queue, Options.Create(settings), NullLogger<BackgroundEmailSender>.Instance), queue);
    }

    private static EmailSettings Configured() => new() { SmtpUser = "user", SmtpPass = "pass" };

    [Fact]
    public void Without_credentials_nothing_is_queued()
    {
        var transport = new RecordingTransport();
        var (sender, queue) = Create(new EmailSettings(), transport);

        sender.Enqueue(Message);

        Assert.Equal(0, queue.Enqueued);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Sends_from_synap_with_html_and_text_parts()
    {
        var transport = new RecordingTransport();
        var (sender, _) = Create(Configured(), transport);

        sender.Enqueue(Message);

        var sent = Assert.Single(transport.Sent);
        var from = Assert.IsType<MailboxAddress>(sent.From.Single());
        Assert.Equal("Synap", from.Name);
        Assert.Equal("no-reply@sergioizq.com", from.Address);
        Assert.Equal("sergio@example.com", sent.To.Mailboxes.Single().Address);
        Assert.Equal("Asunto", sent.Subject);
        Assert.Contains("<p>Hola</p>", sent.HtmlBody);
        Assert.Equal("Hola", sent.TextBody?.Trim());
    }

    [Fact]
    public void An_smtp_failure_is_swallowed_and_logged()
    {
        var (sender, queue) = Create(Configured(), new RecordingTransport(new InvalidOperationException("smtp down")));

        sender.Enqueue(Message);

        Assert.Equal(1, queue.Enqueued);
    }

    [Theory]
    [InlineData("sergio@example.com", "se***@example.com")]
    [InlineData("ab@x.io", "***@x.io")]
    public void Addresses_are_masked_in_logs(string email, string expected)
    {
        Assert.Equal(expected, BackgroundEmailSender.Mask(email));
    }
}
