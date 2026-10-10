using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules.Fluent;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using System.Linq.Expressions;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class RuleStoreConcurrencyTests
{
    private readonly PostgresFixture _pg;
    public RuleStoreConcurrencyTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Save_stale_entity_throws_DbUpdateConcurrencyException()
    {
        await _pg.ResetAsync();
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new NRules.Persistence.EntityFrameworkCore.Options.EFCoreStoreOptions
        {
            TypeResolver = new NRules.Json.TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        var store = new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);

        var builder = new RuleBuilder();
        builder.Name("c1");
        Expression<Func<object, bool>> condition = o => true;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        var rule = builder.Build();
        await store.SaveRuleAsync(rule);

        // 两个并发 ctx 读出同一行
        await using var ctx1 = factory.CreateDbContext();
        await using var ctx2 = factory.CreateDbContext();
        var a = await ctx1.Rules.FindAsync("c1");
        var b = await ctx2.Rules.FindAsync("c1");

        a!.Priority = 100;
        await ctx1.SaveChangesAsync();

        b!.Priority = 200;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await ctx2.SaveChangesAsync());
    }
}
