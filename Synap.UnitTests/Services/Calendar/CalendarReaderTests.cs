using Microsoft.Extensions.Logging.Abstractions;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.UnitTests.Features.Settings;
using System.Security.Cryptography;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// calendar-integration: el despachador delante de los adaptadores y sus cuatro
/// resultados (design.md Decisiones 1 y 2).
/// </summary>
public class CalendarReaderTests
{
    private static readonly DateTime FromUtc = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    private const string CalendarUrl = "https://caldav.example.com/dav/calendars/sergio/default/";

    private readonly CalendarCredentialsProtector _protector =
        new(new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));

    private readonly FakeCalendarConnectionRepository _connections = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeProviderClient _calDav = new(CalendarProvider.CalDav);
    private readonly RecordingLogger<CalendarReader> _logger = new();

    private CalendarReader Reader() =>
        new(_connections, _unitOfWork, _protector, [_calDav], _logger);

    private CalendarConnection Connect(CalendarCredentials credentials, string? encrypted = null) =>
        _connections.Add(CalendarConnection.Create(
            Guid.NewGuid(),
            credentials.Provider,
            "sergio",
            CalendarUrl,
            encrypted ?? _protector.Protect(credentials),
            DateTime.UtcNow));

    [Fact]
    public async Task A_user_with_no_connection_is_not_connected()
    {
        var result = await Reader().ListEventsAsync(Guid.NewGuid(), FromUtc, ToUtc, 10);

        Assert.IsType<CalendarReadResult.NotConnected>(result);
        Assert.Empty(_calDav.Requests);
    }

    [Fact]
    public async Task It_delegates_to_the_provider_of_the_connection_with_its_credentials()
    {
        var credentials = new CalendarCredentials.CalDav("sergio", "secreto");
        var connection = Connect(credentials);
        var demo = new CalendarEvent("Demo", FromUtc.AddHours(9), FromUtc.AddHours(10));
        var standup = new CalendarEvent("Standup", FromUtc.AddHours(8), FromUtc.AddHours(8.25));
        _calDav.Responder = _ => new CalendarProviderRead(new CalendarReadResult.Ok([demo, standup]));

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        var ok = Assert.IsType<CalendarReadResult.Ok>(result);
        Assert.Equal([standup, demo], ok.Events);
        var request = Assert.Single(_calDav.Requests);
        Assert.Equal(CalendarUrl, request.CalendarRef);
        Assert.Equal(credentials, request.Credentials);
        Assert.Equal((FromUtc, ToUtc, 10), (request.FromUtc, request.ToUtc, request.MaxEvents));
    }

    [Fact]
    public async Task It_returns_at_most_the_events_asked_for()
    {
        var connection = Connect(new CalendarCredentials.CalDav("sergio", "secreto"));
        _calDav.Responder = _ => new CalendarProviderRead(new CalendarReadResult.Ok(
            [.. Enumerable.Range(0, 5).Select(i => new CalendarEvent($"E{i}", FromUtc.AddHours(i), FromUtc.AddHours(i + 1)))]));

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 3);

        Assert.Equal(3, Assert.IsType<CalendarReadResult.Ok>(result).Events.Count);
    }

    /// <summary>design.md Decisión 2: si no se puede descifrar, hay que reconectar.</summary>
    [Fact]
    public async Task Credentials_that_cannot_be_decrypted_need_reconnecting()
    {
        var other = new CalendarCredentialsProtector(new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
        var connection = Connect(
            new CalendarCredentials.CalDav("sergio", "secreto"),
            other.Protect(new CalendarCredentials.CalDav("sergio", "secreto")));

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.True(connection.NeedsReconnect);
        Assert.Equal(1, _unitOfWork.SaveCalls);
        Assert.Empty(_calDav.Requests);
    }

    [Fact]
    public async Task A_rejection_from_the_provider_marks_the_connection()
    {
        var connection = Connect(new CalendarCredentials.CalDav("sergio", "secreto"));
        _calDav.Responder = _ => new CalendarProviderRead(new CalendarReadResult.NeedsReconnect());

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.NotNull(connection.NeedsReconnectSince);
        Assert.True(_logger.Mentions("reconectar"));
    }

    [Fact]
    public async Task A_marked_connection_is_not_read_again()
    {
        var connection = Connect(new CalendarCredentials.CalDav("sergio", "secreto"));
        connection.MarkNeedsReconnect(DateTime.UtcNow);

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.Empty(_calDav.Requests);
    }

    /// <summary>specs/calendar-integration "Proveedor caído": no marca la conexión.</summary>
    [Fact]
    public async Task An_unavailable_provider_does_not_mark_the_connection()
    {
        var connection = Connect(new CalendarCredentials.CalDav("sergio", "secreto"));
        _calDav.Responder = _ => new CalendarProviderRead(new CalendarReadResult.Unavailable("HTTP 503"));

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        Assert.Equal("HTTP 503", Assert.IsType<CalendarReadResult.Unavailable>(result).Cause);
        Assert.False(connection.NeedsReconnect);
        Assert.Equal(0, _unitOfWork.SaveCalls);
        Assert.True(_logger.Mentions("HTTP 503"));
    }

    [Fact]
    public async Task An_adapter_that_throws_is_unavailable_without_its_message_in_the_log()
    {
        var connection = Connect(new CalendarCredentials.CalDav("sergio", "secreto"));
        _calDav.Responder = _ => throw new InvalidOperationException("Cena con Marta en casa");

        var result = await Reader().ListEventsAsync(connection.UserId.Value, FromUtc, ToUtc, 10);

        Assert.IsType<CalendarReadResult.Unavailable>(result);
        Assert.False(_logger.Mentions("Cena con Marta"));
    }
}
