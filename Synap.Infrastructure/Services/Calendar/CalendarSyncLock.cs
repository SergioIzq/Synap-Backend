using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Una sincronización por usuario a la vez (calendar-store-and-view design.md Decisión 4): la
/// periódica, la manual y la del briefing no se pisan. Es un lock de Postgres, así que vale
/// también con varias instancias de la API.
/// </summary>
public interface ICalendarSyncLock
{
    /// <summary>El lock, o null si otra sincronización del usuario lo tiene; nunca espera.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// `pg_try_advisory_xact_lock` en una transacción propia, en su propia conexión, que dura lo que la
/// sincronización: las escrituras de cada calendario van en otra conexión con su propia
/// transacción, y soltar el lock es cerrar esta.
/// </summary>
public sealed class PostgresCalendarSyncLock : ICalendarSyncLock
{
    private readonly string _connectionString;

    public PostgresCalendarSyncLock(IConfiguration configuration)
        : this(configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string."))
    {
    }

    public PostgresCalendarSyncLock(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction);
            command.Parameters.AddWithValue("key", KeyOf(userId));

            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new Held(connection, transaction);
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Los 128 bits del id plegados en los 64 del lock.</summary>
    internal static long KeyOf(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes[..8]) ^ BitConverter.ToInt64(bytes[8..]);
    }

    private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Sin token: soltar el lock no debe quedarse a medias porque se canceló la sincronización.
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                // Una conexión rota ya soltó el lock al cerrarse.
            }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
