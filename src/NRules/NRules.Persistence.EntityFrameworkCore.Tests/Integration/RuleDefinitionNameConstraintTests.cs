using Microsoft.EntityFrameworkCore;
using Npgsql;
using NRules.Persistence.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

public class RuleDefinitionNameConstraintTests
{
    [Fact]
    public async Task Rule_definition_name_must_be_present_a_string_and_match_rule_name()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using var ctx = new RulesDbContext(options);
        await ctx.Database.MigrateAsync();

        await InsertRuleAsync(ctx, "matching", """{"name":"matching"}""");

        var invalidDefinitions = new[]
        {
            """{"description":"missing name"}""",
            """{"name":null}""",
            """{"name":123}""",
            """{"name":"different"}"""
        };

        for (var index = 0; index < invalidDefinitions.Length; index++)
        {
            var name = $"invalid_{index}";
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRuleAsync(ctx, name, invalidDefinitions[index]));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("CK_rules_rule_definition_name_matches_name", exception.ConstraintName);
        }
    }

    private static Task<int> InsertRuleAsync(RulesDbContext ctx, string name, string ruleDefinition)
    {
        return ctx.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO rules (name, repeatability, tags, properties, rule_definition) VALUES ({name}, 0, ARRAY[]::text[], '{{}}'::jsonb, {ruleDefinition}::jsonb)");
    }
}
