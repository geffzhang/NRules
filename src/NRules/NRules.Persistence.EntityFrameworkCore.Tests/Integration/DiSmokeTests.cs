using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NRules;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore.DependencyInjection;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class DiSmokeTests
{
    private readonly PostgresFixture _pg;

    public DiSmokeTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_DI_graph_can_resolve_store_and_provider()
    {
        await _pg.ResetAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddNRulesEfCoreStore(
            options =>
            {
                options.TypeResolver = new TypeResolver();
                options.TypeResolver.RegisterDefaultAliases();
            },
            db => db.UseNpgsql(_pg.ConnectionString));

        services.AddNRulesReloader()
            .UsePostgresNotify(options => options.ConnectionString = _pg.ConnectionString);

        await using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<IRuleStore>();
        var provider = serviceProvider.GetRequiredService<IRuleSessionFactoryProvider>();
        var reloader = serviceProvider.GetRequiredService<RulesReloader>();
        var hostedService = Assert.Single(serviceProvider.GetServices<IHostedService>());
        Assert.Same(reloader, hostedService);
        Assert.True(serviceProvider.GetRequiredService<IOptions<ReloaderOptions>>().Value.UseNotify);
        Assert.Equal(
            _pg.ConnectionString,
            serviceProvider.GetRequiredService<IOptions<PostgresNotifyOptions>>().Value.ConnectionString);

        var builder = new RuleBuilder();
        builder.Name("d1");
        Expression<Func<object, bool>> condition = o => true;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        await store.SaveRuleAsync(builder.Build());

        var factory = await RulesReloaderRunner.RunOnceAsync(store, provider, default);
        Assert.NotNull(factory);
    }

    [Fact]
    public async Task Notify_settings_are_optional_when_notifications_are_disabled()
    {
        await _pg.ResetAsync();
        var services = new ServiceCollection();
        services.AddNRulesReloader();

        await using var serviceProvider = services.BuildServiceProvider();
        Assert.False(serviceProvider.GetRequiredService<IOptions<ReloaderOptions>>().Value.UseNotify);
        Assert.Null(serviceProvider.GetRequiredService<IOptions<PostgresNotifyOptions>>().Value.ConnectionString);
    }
}
