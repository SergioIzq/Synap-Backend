using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Domain;

/// <summary>calendar-integration tarea 1.1.</summary>
public class CalendarConnectionTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly UserId User = UserId.Create(Guid.NewGuid()).Value;

    private static CalendarConnection Google()
        => CalendarConnection.Create(User, CalendarProvider.Google, " ana@example.com ", "primary", "cifrado", Now);

    [Fact]
    public void Una_conexion_nueva_funciona_y_pertenece_al_usuario()
    {
        var connection = Google();

        Assert.Equal(User, connection.Id);
        Assert.Equal("ana@example.com", connection.AccountLabel);
        Assert.Equal(Now, connection.ConnectedAt);
        Assert.False(connection.NeedsReconnect);
    }

    [Fact]
    public void Marcar_la_reconexion_conserva_el_primer_momento()
    {
        var connection = Google();

        connection.MarkNeedsReconnect(Now.AddHours(1));
        connection.MarkNeedsReconnect(Now.AddHours(5));

        Assert.True(connection.NeedsReconnect);
        Assert.Equal(Now.AddHours(1), connection.NeedsReconnectSince);
    }

    [Fact]
    public void Limpiar_la_marca_vuelve_a_dejarla_funcionando()
    {
        var connection = Google();
        connection.MarkNeedsReconnect(Now);

        connection.ClearNeedsReconnect();

        Assert.False(connection.NeedsReconnect);
    }

    [Fact]
    public void Reconectar_con_otro_proveedor_sustituye_todo_y_quita_la_marca()
    {
        var connection = Google();
        connection.MarkNeedsReconnect(Now);

        connection.Reconnect(CalendarProvider.CalDav, "ana", "https://dav.example.com/cal/", "otro", Now.AddDays(1));

        Assert.Equal(CalendarProvider.CalDav, connection.Provider);
        Assert.Equal("https://dav.example.com/cal/", connection.CalendarRef);
        Assert.Equal("otro", connection.CredentialsEncrypted);
        Assert.Equal(Now.AddDays(1), connection.ConnectedAt);
        Assert.False(connection.NeedsReconnect);
    }

    [Fact]
    public void Renovar_credenciales_no_cambia_la_conexion()
    {
        var connection = Google();

        connection.UpdateCredentials("renovado");

        Assert.Equal("renovado", connection.CredentialsEncrypted);
        Assert.Equal(Now, connection.ConnectedAt);
    }

    [Fact]
    public void Un_evento_de_todo_el_dia_lo_dice()
    {
        var allDay = new CalendarEvent("Viaje", Now, Now.AddDays(1), new DateOnly(2026, 11, 14), null, null);
        var timed = new CalendarEvent("Demo", Now, Now.AddHours(1), null, null, null);

        Assert.True(allDay.IsAllDay);
        Assert.False(timed.IsAllDay);
    }
}
