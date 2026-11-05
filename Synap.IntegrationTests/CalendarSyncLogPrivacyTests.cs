using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Security.Cryptography;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-store-and-view design.md Decisión 8 - la sincronización no escribe en los registros
/// ningún SUMMARY, DESCRIPTION, LOCATION ni .ics en claro.
/// Complementa CalendarLogPrivacyTests (capa CalDavCalendarClient).
/// Los endpoints nuevos no usan logger, así que la ausencia de contenido sensible en sus trazas
/// se verifica aquí a través del RecordingLogSink del ApiFixture.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CalendarSyncLogPrivacyTests
{
    private const string Home = "https://dav.example.test/privacy/";
    private const string CalendarUrl = "https://dav.example.test/privacy/personal/";

    private const string SensitiveTitle = "Cita con el oncólogo";
    private const string SensitiveDescription = "Llevar los análisis de sangre";
    private const string SensitiveLocation = "Hospital de la Paz sala 42";

    private static readonly string SensitiveIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
        "BEGIN:VEVENT\r\nUID:privacy-test-event\r\nDTSTAMP:20261001T120000Z\r\n" +
        "DTSTART:20261114T090000Z\r\nDTEND:20261114T100000Z\r\n" +
        $"SUMMARY:{SensitiveTitle}\r\n" +
        $"DESCRIPTION:{SensitiveDescription}\r\n" +
        $"LOCATION:{SensitiveLocation}\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private readonly PostgresFixture _fixture;
    private readonly ISecretProtector _protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

    public CalendarSyncLogPrivacyTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// La sincronización almacena el título, la descripción y la ubicación en la BD pero no los
    /// escribe en ningún registro de log (design.md Decisión 8).
    /// </summary>
    [Fact]
    public async Task Syncing_events_with_sensitive_content_produces_no_sensitive_log_lines()
    {
        var userId = await CreateConnectionAsync();
        var log = new RecordingLogger<CalendarSynchronizer>();

        var client = new SyncClientStub(
            listCalendars: _ => new CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok(
                [new(CalendarUrl, "Personal", null, "ctag-1", true, false)]),
            syncCollection: (_, _, _) => new CalDavResult<SyncCollectionChanges>.Ok(
                new SyncCollectionChanges(
                    [new RemoteETag(CalendarUrl + "event.ics", "\"e1\"")], [], "tok-1", false)),
            multiget: (_, _, _) => new CalDavResult<MultigetResult>.Ok(
                new MultigetResult([new RemoteResource(CalendarUrl + "event.ics", "\"e1\"", SensitiveIcs)], [])),
            listEtags: _ => new CalDavResult<IReadOnlyList<RemoteETag>>.Ok([]));

        await using var context = _fixture.CreateContext();
        var synchronizer = new CalendarSynchronizer(
            context,
            new NoOpSyncLock(),
            client,
            new NoOpDiscovery(Home),
            new CalendarCredentialsProtector(_protector),
            log);

        await synchronizer.SyncAsync(userId);

        foreach (var secret in new[] { SensitiveTitle, SensitiveDescription, SensitiveLocation, "BEGIN:VCALENDAR" })
        {
            Assert.DoesNotContain(log.Records, r => r.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<Guid> CreateConnectionAsync()
    {
        await using var context = _fixture.CreateContext();
        var user = User.Create(
            Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"),
            PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().Add(user);

        var creds = new CalendarCredentialsProtector(_protector)
            .Protect(new CalendarCredentials.CalDav("test@example.com", "app-pass"));
        context.Set<CalendarConnection>().Add(
            CalendarConnection.Create(user.Id.Value, CalendarProvider.CalDav, "test@example.com", Home, creds, DateTime.UtcNow));

        await context.SaveChangesAsync();
        return user.Id.Value;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(formatter(state, exception));
            if (exception is not null) Records.Add(exception.ToString());
        }
    }

    private sealed class NoOpSyncLock : ICalendarSyncLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(new NoOpDisposable());
        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpDiscovery(string home) : ICalDavDiscovery
    {
        public Task<CalDavDiscoveryResult> DiscoverAsync(
            Uri url, string username, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult<CalDavDiscoveryResult>(new CalDavDiscoveryResult.Found(home));
    }

    private sealed class SyncClientStub(
        Func<Uri, CalDavResult<IReadOnlyList<RemoteCalendar>>> listCalendars,
        Func<Uri, CalendarCredentials.CalDav, string?, CalDavResult<SyncCollectionChanges>> syncCollection,
        Func<Uri, CalendarCredentials.CalDav, IReadOnlyList<string>, CalDavResult<MultigetResult>> multiget,
        Func<Uri, CalDavResult<IReadOnlyList<RemoteETag>>> listEtags)
        : ICalDavSyncClient
    {
        public Task<CalDavResult<IReadOnlyList<RemoteCalendar>>> ListCalendarsAsync(
            Uri home, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken) =>
            Task.FromResult(listCalendars(home));
        public Task<CalDavResult<SyncCollectionChanges>> SyncCollectionAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, string? syncToken, CancellationToken cancellationToken) =>
            Task.FromResult(syncCollection(calendar, credentials, syncToken));
        public Task<CalDavResult<MultigetResult>> MultigetAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, IReadOnlyList<string> hrefs, CancellationToken cancellationToken) =>
            Task.FromResult(multiget(calendar, credentials, hrefs));
        public Task<CalDavResult<IReadOnlyList<RemoteETag>>> ListETagsAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken) =>
            Task.FromResult(listEtags(calendar));
    }
}
