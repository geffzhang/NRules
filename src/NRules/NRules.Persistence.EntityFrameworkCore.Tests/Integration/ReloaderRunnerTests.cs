using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using NRules;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class ReloaderRunnerTests
{
    private readonly PostgresFixture _pg;

    public ReloaderRunnerTests(PostgresFixture pg) => _pg = pg;

    private static IRuleDefinition MakeRule(string name)
    {
        var builder = new RuleBuilder();
        builder.Name(name);
        Expression<Func<object, bool>> condition = o => true;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        return builder.Build();
    }

    [Fact]
    public async Task RunOnceAsync_compiles_rules_and_swaps_factory()
    {
        await _pg.ResetAsync();
        var store = await MakeStoreAsync();
        var provider = new RuleSessionFactoryProvider();

        await store.SaveRuleAsync(MakeRule("r1"));
        await store.SaveRuleAsync(MakeRule("r2"));

        var factory = await RulesReloaderRunner.RunOnceAsync(store, provider, default);

        Assert.NotNull(factory);
        Assert.Same(factory, provider.Current);
    }

    private Task<IRuleStore> MakeStoreAsync()
    {
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var options = new EFCoreStoreOptions
        {
            TypeResolver = new TypeResolver(),
        };
        options.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(options);
        IRuleStore store = new EFCoreRuleStore(
            factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
        return Task.FromResult(store);
    }
}
