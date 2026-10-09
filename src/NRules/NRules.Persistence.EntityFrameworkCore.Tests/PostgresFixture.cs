using Microsoft.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();

    public RulesDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new RulesDbContext(options);
    }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nrules_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _container.StartAsync();

        await using var context = NewDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var context = NewDbContext();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"rules\"");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
