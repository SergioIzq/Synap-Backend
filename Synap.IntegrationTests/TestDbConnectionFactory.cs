using Npgsql;
using Synap.Shared.Application.Interfaces;
using System.Data;

namespace Synap.IntegrationTests;

public sealed class TestDbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public TestDbConnectionFactory(string connectionString) => _connectionString = connectionString;

    public IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);
}
