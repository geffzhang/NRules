using System.Linq.Expressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
public class ReloaderPollingTests
{
    private readonly PostgresFixture _pg;

    public ReloaderPollingTests(PostgresFixture pg) => _pg = pg;

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
    public async Task Polling_picks_up_insert_after_interval()
    {
        await _pg.ResetAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore();
        var provider = new RuleSessionFactoryProvider();
        var reloader = new RulesReloader(
            store,
            provider,
            new ReloaderOptions
            {
                PollInterval = TimeSpan.FromSeconds(10),
                PollJitter = TimeSpan.Zero,
            },
            time,
            NullLoggerFactory.Instance);

        await store.SaveRuleAsync(MakeRule("before-start"));
        var swaps = Channel.CreateUnbounded<ISessionFactory>();
        provider.FactorySwapped += (_, factory) => swaps.Writer.TryWrite(factory);

        await reloader.StartAsync(CancellationToken.None);
        try
        {
            var initial = await swaps.Reader.ReadAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await store.SaveRuleAsync(MakeRule("after-start"));
            time.Advance(TimeSpan.FromSeconds(10));
            var reloaded = await swaps.Reader.ReadAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotSame(initial, reloaded);
        }
        finally
        {
            await reloader.StopAsync(CancellationToken.None);
        }
    }
}
