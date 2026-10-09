using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NRules;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public static class RulesReloaderRunner
{
    public static Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        CancellationToken ct) =>
        RunOnceAsync(
            store,
            provider,
            ct,
            exception => Trace.TraceError(
                "FactorySwapped event handler failed after factory publication; treating reload as successful. {0}",
                exception));

    public static Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return RunOnceAsync(
            store,
            provider,
            ct,
            exception => logger.LogError(
                exception,
                "FactorySwapped event handler failed after factory publication; treating reload as successful"));
    }

    private static async Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        CancellationToken ct,
        Action<Exception> logSubscriberFailure)
    {
        var rules = await store.LoadAllAsync(ct);
        var factory = new RuleCompiler().Compile(rules);
        try
        {
            provider.Swap(factory);
        }
        catch (Exception exception) when (ReferenceEquals(provider.Current, factory))
        {
            logSubscriberFailure(exception);
        }

        return factory;
    }
}
