using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Entities;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class EFCoreRuleStoreResilienceTests
{
    private readonly PostgresFixture _pg;

    public EFCoreRuleStoreResilienceTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task RunOnce_preserves_current_factory_when_rule_cannot_be_loaded()
    {
        await _pg.ResetAsync();
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var options = new NRules.Persistence.EntityFrameworkCore.Options.EFCoreStoreOptions
        {
            TypeResolver = new NRules.Json.TypeResolver(),
        };
        options.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(options);
        var store = new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
        var provider = new RuleSessionFactoryProvider();

        var ruleBuilder = new RuleBuilder();
        ruleBuilder.Name("good");
        Expression<Func<object, bool>> condition = o => true;
        ruleBuilder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        ruleBuilder.RightHandSide().Action(action);
        var good = ruleBuilder.Build();
        await store.SaveRuleAsync(good);
        var original = await RulesReloaderRunner.RunOnceAsync(store, provider, default);

        await using var context = factory.CreateDbContext();
        context.Rules.Add(new RuleEntity
        {
            Name = "broken",
            RuleDefinition = JsonDocument.Parse("{\"name\":\"broken\"}"),
            Properties = JsonDocument.Parse("{}"),
            Tags = Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RulesReloaderRunner.RunOnceAsync(store, provider, default));
        Assert.Contains("broken", error.Message);
        Assert.Same(original, provider.Current);

        context.Rules.Remove(await context.Rules.FindAsync("broken")
            ?? throw new InvalidOperationException("Broken test row was not found."));
        await context.SaveChangesAsync();

        var retried = await RulesReloaderRunner.RunOnceAsync(store, provider, default);
        Assert.NotSame(original, retried);
        Assert.Same(retried, provider.Current);
    }
}
