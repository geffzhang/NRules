using NRules;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public static class RulesReloaderRunner
{
    public static async Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        CancellationToken ct)
    {
        var rules = await store.LoadAllAsync(ct);
        var factory = new RuleCompiler().Compile(rules);
        provider.Swap(factory);
        return factory;
    }
}
