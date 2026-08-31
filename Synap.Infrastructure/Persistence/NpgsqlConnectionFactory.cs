using Microsoft.Extensions.Configuration;
using Npgsql;
using Synap.Shared.Application.Interfaces;
using System.Data;

namespace Synap.Infrastructure.Persistence;

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string.");
    }

    public IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);
}
