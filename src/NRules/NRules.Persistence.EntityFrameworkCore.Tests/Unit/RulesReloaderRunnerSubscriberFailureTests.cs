using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Moq;
using NRules;
using NRules.Persistence.EntityFrameworkCore;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class RulesReloaderRunnerSubscriberFailureTests
{
    [Fact]
    public async Task Logger_overload_returns_published_factory_and_logs_subscriber_failure()
    {
        var store = new Mock<IRuleStore>();
        store.Setup(x => x.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeRule() });
        var provider = new RuleSessionFactoryProvider();
        var subscriberException = new InvalidOperationException("subscriber failure");
        provider.FactorySwapped += (_, _) => throw subscriberException;
        var logger = new CapturingLogger();
        var readiness = provider.WaitForReadyAsync();

        var result = await RulesReloaderRunner.RunOnceAsync(
            store.Object,
            provider,
            logger,
            CancellationToken.None);

        Assert.Same(result, provider.Current);
        Assert.Same(result, await readiness);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(subscriberException, entry.Exception);
        Assert.Contains("FactorySwapped", entry.Message, StringComparison.Ordinal);
        Assert.Contains("after factory publication", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_overloads_propagate_swap_failure_before_publication(bool useLogger)
    {
        var store = new Mock<IRuleStore>();
        store.Setup(x => x.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeRule() });
        var exception = new InvalidOperationException("publication failed");
        var provider = new Mock<IRuleSessionFactoryProvider>();
        provider.SetupGet(x => x.Current).Returns(Mock.Of<ISessionFactory>());
        provider.Setup(x => x.Swap(It.IsAny<ISessionFactory>())).Throws(exception);
        var logger = new CapturingLogger();

        var observedException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useLogger
                ? RulesReloaderRunner.RunOnceAsync(store.Object, provider.Object, logger, CancellationToken.None)
                : RulesReloaderRunner.RunOnceAsync(store.Object, provider.Object, CancellationToken.None));

        Assert.Same(exception, observedException);
        Assert.Empty(logger.Entries);
    }

    private static IRuleDefinition MakeRule()
    {
        var builder = new RuleBuilder();
        builder.Name("runner-subscriber-test");
        Expression<Func<object, bool>> condition = o => true;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        return builder.Build();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message, Exception? Exception)> Entries =>
            _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }
}
