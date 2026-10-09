using Microsoft.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class MigrationAppliesTests
{
    private readonly PostgresFixture _pg;

    public MigrationAppliesTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Schema_apply_succeeds_and_table_is_queryable()
    {
        await _pg.ResetAsync();
        await using var ctx = _pg.NewDbContext();
        var migrations = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, migration => migration.EndsWith("_InitRules", StringComparison.Ordinal));
        var rows = await ctx.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"rules\"").ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0] >= 0);
    }
}
