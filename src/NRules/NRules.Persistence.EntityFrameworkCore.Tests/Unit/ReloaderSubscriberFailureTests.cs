using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NRules;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class ReloaderSubscriberFailureTests
{
    [Fact]
    public async Task Throwing_factory_swapped_handler_keeps_publication_successful_and_skips_unchanged_fingerprint()
    {
        var fingerprint = new RulesFingerprint(123, 1, 1);
        var rules = new[] { MakeRule() };
        var secondFingerprintRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fingerprintCalls = 0;
        var store = new Mock<IRuleStore>();
        store.Setup(x => x.ComputeFingerprintAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref fingerprintCalls) == 2)
                    secondFingerprintRequested.TrySetResult();
                return Task.FromResult(fingerprint);
            });
        store.Setup(x => x.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

        var loggerFactory = new CapturingLoggerFactory();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new RuleSessionFactoryProvider();
        var subscriberCalls = 0;
        provider.FactorySwapped += (_, _) =>
        {
            Interlocked.Increment(ref subscriberCalls);
            throw new InvalidOperationException("subscriber failure");
        };

        using var reloader = new RulesReloader(
            store.Object,
            provider,
            new ReloaderOptions
            {
                PollInterval = TimeSpan.FromSeconds(10),
                PollJitter = TimeSpan.Zero,
            },
            time,
            loggerFactory);

        await reloader.StartAsync(CancellationToken.None);
        var readyFactory = await provider.WaitForReadyAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(readyFactory, provider.Current);

        time.Advance(TimeSpan.FromSeconds(10));
        await secondFingerprintRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reloader.StopAsync(CancellationToken.None);

        Assert.Same(readyFactory, provider.Current);
        Assert.Equal(1, subscriberCalls);
        store.Verify(x => x.ComputeFingerprintAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        store.Verify(x => x.LoadAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(
            loggerFactory.Messages,
            message => message.Contains("FactorySwapped event handler failed after factory publication", StringComparison.Ordinal));
        Assert.DoesNotContain(
            loggerFactory.Messages,
            message => message.Contains("Rules rebuild failed", StringComparison.Ordinal));
    }

    private static IRuleDefinition MakeRule()
    {
        var builder = new RuleBuilder();
        builder.Name("subscriber-test");
        Expression<Func<object, bool>> condition = o => true;
        builder.LeftHandSide().Pattern(typeof(object), "o").Condition(condition);
        Expression<Action<IContext>> action = ctx => GC.KeepAlive(ctx);
        builder.RightHandSide().Action(action);
        return builder.Build();
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
            }
        }
    }
}
