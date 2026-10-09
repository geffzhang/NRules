namespace NRules.Persistence.EntityFrameworkCore;

public readonly record struct RulesFingerprint(long Hash, int Count, int LatestVersion);
