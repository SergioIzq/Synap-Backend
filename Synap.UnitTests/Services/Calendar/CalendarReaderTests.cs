using Microsoft.Extensions.Logging.Abstractions;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>calendar-integration tarea 1.3: los cuatro resultados del despachador.</summary>
public class CalendarReaderTests
{
    private static readonly DateTime From = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);
    private static readonly UserId User = UserId.Create(Guid.NewGuid()).Value;

    private readonly FakeCalendarConnections _connections = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeProvider _google = new(CalendarProvider.Google);

    private CalendarReader Reader() => new(
        _connections, _unitOfWork, new FakeSecretProtector(), [_google], NullLogger<CalendarReader>.Instance);

    private CalendarConnection Connect(string credentials = "enc({})")
    {
        var connection = CalendarConnection.Create(User, CalendarProvider.Google, "ana@example.com", "primary", credentials, From);
        _connections.Stored = connection;
        return connection;
    }

    private static CalendarEvent Event(string title, int hour)
        => new(title, From.AddHours(hour), From.AddHours(hour + 1), null, null, null);

    [Fact]
    public async Task Sin_conexion_es_NotConnected_y_no_llama_al_proveedor()
    {
        var result = await Reader().ListEventsAsync(User.Value, From, To, 10);

        Assert.IsType<CalendarReadResult.NotConnected>(result);
        Assert.Equal(0, _google.Calls);
    }

    [Fact]
    public async Task Los_eventos_vuelven_ordenados_y_recortados()
    {
        Connect();
        _google.Result = new ProviderReadResult.Ok([Event("Tarde", 15), Event("Mañana", 9), Event("Mediodía", 12)]);

        var result = await Reader().ListEventsAsync(User.Value, From, To, 2);

        var ok = Assert.IsType<CalendarReadResult.Ok>(result);
        Assert.Equal(["Mañana", "Mediodía"], ok.Events.Select(e => e.Title));
        Assert.Equal(2, _google.LastMax);
    }

    [Fact]
    public async Task Un_token_renovado_se_guarda_cifrado()
    {
        var connection = Connect();
        _google.Result = new ProviderReadResult.Ok([], RenewedCredentials: "{\"nuevo\":1}");

        await Reader().ListEventsAsync(User.Value, From, To, 10);

        Assert.Equal("enc({\"nuevo\":1})", connection.CredentialsEncrypted);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Credenciales_rechazadas_marcan_la_reconexion()
    {
        var connection = Connect();
        _google.Result = new ProviderReadResult.Rejected("invalid_grant");

        var result = await Reader().ListEventsAsync(User.Value, From, To, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.True(connection.NeedsReconnect);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Credenciales_que_no_se_descifran_marcan_la_reconexion()
    {
        var connection = Connect(credentials: "basura");

        var result = await Reader().ListEventsAsync(User.Value, From, To, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.True(connection.NeedsReconnect);
        Assert.Equal(0, _google.Calls);
    }

    [Fact]
    public async Task Una_conexion_ya_marcada_no_se_vuelve_a_intentar()
    {
        Connect().MarkNeedsReconnect(From);

        var result = await Reader().ListEventsAsync(User.Value, From, To, 10);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(result);
        Assert.Equal(0, _google.Calls);
    }

    [Fact]
    public async Task Un_fallo_temporal_no_marca_la_conexion()
    {
        var connection = Connect();
        _google.Result = new ProviderReadResult.Unavailable("proveedor inalcanzable");

        var result = await Reader().ListEventsAsync(User.Value, From, To, 10);

        var unavailable = Assert.IsType<CalendarReadResult.Unavailable>(result);
        Assert.Equal("proveedor inalcanzable", unavailable.Cause);
        Assert.False(connection.NeedsReconnect);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Un_intervalo_demasiado_largo_se_acota()
    {
        Connect();
        _google.Result = new ProviderReadResult.Ok([]);

        await Reader().ListEventsAsync(User.Value, From, From.AddYears(1), 10);

        Assert.Equal(From.AddDays(92), _google.LastTo);
    }

    private sealed class FakeProvider(CalendarProvider provider) : ICalendarProviderClient
    {
        public CalendarProvider Provider { get; } = provider;
        public ProviderReadResult Result { get; set; } = new ProviderReadResult.Ok([]);
        public int Calls { get; private set; }
        public int LastMax { get; private set; }
        public DateTime LastTo { get; private set; }

        public Task<ProviderReadResult> ListEventsAsync(
            string calendarRef, string credentials, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken)
        {
            Calls++;
            LastMax = maxEvents;
            LastTo = toUtc;
            return Task.FromResult(Result);
        }
    }
}

internal sealed class FakeCalendarConnections : ICalendarConnectionRepository
{
    public CalendarConnection? Stored { get; set; }

    public Task<CalendarConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Stored is not null && Stored.Id.Value == id ? Stored : null);

    public void Add(CalendarConnection entity) => Stored = entity;

    public Task CreateAsync(CalendarConnection entity, CancellationToken cancellationToken)
    {
        Stored = entity;
        return Task.CompletedTask;
    }

    public void Update(CalendarConnection entity)
    {
    }

    public void Delete(CalendarConnection entity) => Stored = null;
}
