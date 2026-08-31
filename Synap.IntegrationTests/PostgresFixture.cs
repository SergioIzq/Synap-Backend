using Microsoft.EntityFrameworkCore;
using Synap.Infrastructure.Persistence.Command;
using Testcontainers.PostgreSql;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// Spins up a real `pgvector/pgvector:pg16` container (matching docker-compose.yml) and applies
/// every EF Core migration, so these tests exercise actual Postgres behavior - not a fake/in-memory
/// provider that wouldn't catch a real isolation bug in a query filter.
///
/// Requires Docker. Could not be run at all in the original development environment (no local
/// Docker daemon there) - written carefully against the real schema, but genuinely unverified
/// until run somewhere with Docker (e.g. `dotnet test` on the VPS, or in CI).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("synap_test")
        .WithUsername("synap_test")
        .WithPassword("synap_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public SynapDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SynapDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new SynapDbContext(options);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
