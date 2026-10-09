using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NRules.Persistence.EntityFrameworkCore;

public sealed class RulesDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RulesDbContext>
{
    public RulesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NRULES_DESIGN_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The NRULES_DESIGN_CONNECTION_STRING environment variable must be set to generate EF Core migrations.");
        }

        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new RulesDbContext(options);
    }
}
