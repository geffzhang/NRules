using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public interface IRuleStore
{
    Task SaveRuleAsync(IRuleDefinition rule, CancellationToken ct = default);
    Task DeleteRuleAsync(string name, CancellationToken ct = default);
    Task<IRuleDefinition?> LoadAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<IRuleDefinition>> LoadAllAsync(CancellationToken ct = default);
    Task<RulesFingerprint> ComputeFingerprintAsync(CancellationToken ct = default);
}
