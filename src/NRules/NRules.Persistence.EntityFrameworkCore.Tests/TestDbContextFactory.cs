using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NRules.Persistence.EntityFrameworkCore;

namespace NRules.Persistence.EntityFrameworkCore.Tests;

internal sealed class TestDbContextFactory : IDbContextFactory<RulesDbContext>
{
    private readonly DbContextOptions<RulesDbContext> _options;

    public TestDbContextFactory(string connectionString)
        => _options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(connectionString)
            .Options;

    public RulesDbContext CreateDbContext() => new(_options);
}
