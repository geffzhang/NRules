using System.Linq.Expressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NRules;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class ReloaderNotifyTests
{
    private readonly PostgresFixture _pg;

    public ReloaderNotifyTests(PostgresFixture pg) => _pg = pg;

    private IRuleStore MakeStore()
    {
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var options = new EFCoreStoreOptions
        {
            TypeResolver = new TypeResolver(),
        };
        options.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(options);
        return new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
    }

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
    public async Task Direct_sql_update_rebuilds_factory_without_waiting_for_poll()
    {
        await _pg.ResetAsync();
        var store = MakeStore();
        await store.SaveRuleAsync(MakeRule("notify-rule"));
        await store.SaveRuleAsync(MakeRule("survivor-rule"));

        var provider = new RuleSessionFactoryProvider();
        var swaps = Channel.CreateUnbounded<ISessionFactory>();
        provider.FactorySwapped += (_, factory) => swaps.Writer.TryWrite(factory);
        var reloader = new RulesReloader(
            store,
            provider,
            new ReloaderOptions
            {
                UseNotify = true,
                PollInterval = TimeSpan.FromMinutes(1),
                PollJitter = TimeSpan.Zero,
            },
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new PostgresNotifyOptions
            {
                ConnectionString = _pg.ConnectionString,
            });

        await reloader.StartAsync(CancellationToken.None);
        try
        {
            await reloader.WaitForNotifyReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var initial = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await using var conn = new Npgsql.NpgsqlConnection(_pg.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE rules SET rule_definition = rule_definition WHERE name = 'notify-rule'",
                conn);
            await cmd.ExecuteNonQueryAsync();

            var reloaded = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(initial, reloaded);

            cmd.CommandText = "DELETE FROM rules WHERE name = 'notify-rule'";
            await cmd.ExecuteNonQueryAsync();
            var afterDelete = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(reloaded, afterDelete);
            Assert.Single(await store.LoadAllAsync());
        }
        finally
        {
            await reloader.StopAsync(CancellationToken.None);
        }
    }
}
