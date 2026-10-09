using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules.Fluent;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;
using System.Linq.Expressions;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class EFCoreRuleStoreCrudTests
{
    private readonly PostgresFixture _pg;

    public EFCoreRuleStoreCrudTests(PostgresFixture pg) => _pg = pg;

    private IRuleDefinition MakeRule(string name, string? description = null, int priority = 42)
    {
        var builder = new RuleBuilder();
        builder.Name(name);
        builder.Description(description ?? $"Description for {name}");
        builder.Priority(priority);
        builder.Repeatability(RuleRepeatability.NonRepeatable);
        builder.Tag("persisted");
        builder.Property("source", "integration test");
        Expression<Func<object, bool>> condition = o => o.GetHashCode() == 0;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        return builder.Build();
    }

    private EFCoreRuleStore NewStore()
    {
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new EFCoreStoreOptions
        {
            TypeResolver = new TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        return new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
    }

    [Fact]
    public async Task SaveRule_Then_LoadAll_Roundtrips_Equivalent_Definition()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        var rule = MakeRule("rule-A");

        await store.SaveRuleAsync(rule);
        var all = await store.LoadAllAsync();

        Assert.Single(all);
        Assert.Equal(rule.Name, all[0].Name);
        Assert.Equal(rule.Description, all[0].Description);
        Assert.Equal(rule.Priority, all[0].Priority);
        Assert.Equal(rule.Repeatability, all[0].Repeatability);
        Assert.Equal(rule.Tags, all[0].Tags);
        Assert.Equal(rule.Properties["source"], all[0].Properties["source"]);

        await using var ctx = _pg.NewDbContext();
        var entity = await ctx.Rules.SingleAsync();
        Assert.Equal(rule.Name, entity.Name);
        Assert.Equal(rule.Description, entity.Description);
        Assert.Equal(rule.Priority, entity.Priority);
        Assert.Equal(rule.Repeatability, entity.Repeatability);
        Assert.Equal(rule.Tags, entity.Tags);
        Assert.Equal("rule-A", entity.RuleDefinition.RootElement.GetProperty("name").GetString());
        Assert.NotNull(new RuleCompiler().Compile(all));
    }

    [Fact]
    public async Task DeleteRule_Removes_Row()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        var rule = MakeRule("rule-B");
        await store.SaveRuleAsync(rule);

        await store.DeleteRuleAsync("rule-B");

        Assert.Null(await store.LoadAsync("rule-B"));
    }

    [Fact]
    public async Task SaveRule_Updates_Existing_Row_And_Increments_Version()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        await store.SaveRuleAsync(MakeRule("rule-update"));

        var updatedRule = MakeRule("rule-update", "Updated description", 67);
        await store.SaveRuleAsync(updatedRule);

        var loaded = await store.LoadAsync("rule-update");
        await using var ctx = _pg.NewDbContext();
        var entity = await ctx.Rules.SingleAsync();
        Assert.NotNull(loaded);
        Assert.Equal(updatedRule.Description, entity.Description);
        Assert.Equal(updatedRule.Priority, entity.Priority);
        Assert.Equal(updatedRule.Priority, loaded.Priority);
        Assert.Equal(2, entity.RuleVersion);
        Assert.Equal(2, (await store.ComputeFingerprintAsync()).LatestVersion);
    }

    [Fact]
    public async Task LoadAll_Fails_Without_Returning_A_Partial_Ruleset()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        await store.SaveRuleAsync(MakeRule("valid-rule"));

        await using (var ctx = _pg.NewDbContext())
        {
            const string definition = """{"name":"invalid-rule"}""";
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO rules (name, repeatability, tags, properties, rule_definition) VALUES ('invalid-rule', 0, ARRAY[]::text[], '{{}}'::jsonb, {definition}::jsonb)");
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAllAsync());

        Assert.Contains("no partial ruleset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compute_Fingerprint_Is_Stable_Across_Equal_Inputs()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        await store.SaveRuleAsync(MakeRule("rule-C"));
        await using (var context = _pg.NewDbContext())
        {
            var row = await context.Rules.SingleAsync();
            Assert.Equal("rule-C", row.Name);
            Assert.Equal(1, row.RuleVersion);
        }

        var f1 = await store.ComputeFingerprintAsync();
        var f2 = await store.ComputeFingerprintAsync();

        Assert.Equal(f1.Hash, f2.Hash);
        Assert.Equal(-3564688325343655662L, f1.Hash);
        Assert.Equal(1, f1.Count);
        Assert.Equal(1, f1.LatestVersion);
    }

    [Fact]
    public async Task SaveRule_Roundtrips_MemberInit_Assignment_And_Compiles()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        var builder = new RuleBuilder();
        builder.Name("member-init");
        builder.LeftHandSide().Pattern(typeof(ProjectionFact), "fact");
        Expression<Action<IContext, ProjectionFact>> action =
            (context, fact) => context.Insert(new ProjectionResult { Value = fact.Value });
        builder.RightHandSide().Action(action);
        await store.SaveRuleAsync(builder.Build());

        var rules = await store.LoadAllAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("member-init", rule.Name);
        Assert.NotNull(new RuleCompiler().Compile(rules));
    }
}

public sealed class ProjectionFact
{
    public string Value { get; set; } = string.Empty;
}

public sealed class ProjectionResult
{
    public string Value { get; set; } = string.Empty;
}
