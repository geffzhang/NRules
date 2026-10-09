using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NRules.Persistence.EntityFrameworkCore.Entities;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public class EFCoreRuleStore : IRuleStore
{
    private readonly IDbContextFactory<RulesDbContext> _factory;
    private readonly JsonSerializerOptions _serializer;
    private readonly ILogger<EFCoreRuleStore> _logger;

    public EFCoreRuleStore(
        IDbContextFactory<RulesDbContext> factory,
        JsonSerializerOptions serializer,
        ILogger<EFCoreRuleStore> logger)
    {
        _factory = factory;
        _serializer = serializer;
        _logger = logger;
    }

    public async Task SaveRuleAsync(IRuleDefinition rule, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(rule, _serializer);
        var existing = await ctx.Rules.FindAsync(new object?[] { rule.Name }, ct);

        if (existing is null)
        {
            var entity = new RuleEntity
            {
                Name = rule.Name,
                Description = rule.Description,
                Priority = rule.Priority,
                Repeatability = rule.Repeatability,
                Tags = rule.Tags.ToArray(),
                Properties = JsonDocument.Parse(JsonSerializer.Serialize(rule.Properties, _serializer)),
                RuleDefinition = JsonDocument.Parse(json),
            };
            await ctx.Rules.AddAsync(entity, ct);
        }
        else
        {
            existing.Description = rule.Description;
            existing.Priority = rule.Priority;
            existing.Repeatability = rule.Repeatability;
            existing.Tags = rule.Tags.ToArray();
            existing.Properties = JsonDocument.Parse(
                JsonSerializer.Serialize(rule.Properties, _serializer));
            existing.RuleDefinition = JsonDocument.Parse(json);
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteRuleAsync(string name, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.Rules.FindAsync(new object?[] { name }, ct);
        if (existing is null)
            return;

        ctx.Rules.Remove(existing);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IRuleDefinition?> LoadAsync(string name, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.FirstOrDefault(rule => rule.Name == name);
    }

    public async Task<IReadOnlyList<IRuleDefinition>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Rules.AsNoTracking().ToListAsync(ct);
        var result = new List<IRuleDefinition>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                var definition = JsonSerializer.Deserialize<IRuleDefinition>(
                    RuleJsonOrderNormalizer.Normalize(row.RuleDefinition.RootElement.GetRawText()), _serializer);
                if (definition is null)
                    throw new InvalidOperationException("Deserialization returned null.");
                if (!string.Equals(definition.Name, row.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Definition name '{definition.Name}' does not match row name '{row.Name}'.");
                result.Add(definition);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot load rule {Rule}; aborting the ruleset load", row.Name);
                throw new InvalidOperationException(
                    $"Cannot load rule '{row.Name}'; no partial ruleset will be returned.", ex);
            }
        }

        return result;
    }

    public async Task<RulesFingerprint> ComputeFingerprintAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Rules.AsNoTracking()
            .OrderBy(rule => rule.Name)
            .Select(rule => new { rule.Name, rule.RuleVersion })
            .ToListAsync(ct);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> valueBuffer = stackalloc byte[sizeof(int)];
        foreach (var row in rows)
        {
            var nameBytes = Encoding.UTF8.GetBytes(row.Name);
            BinaryPrimitives.WriteInt32BigEndian(valueBuffer, nameBytes.Length);
            hash.AppendData(valueBuffer);
            hash.AppendData(nameBytes);
            BinaryPrimitives.WriteInt32BigEndian(valueBuffer, row.RuleVersion);
            hash.AppendData(valueBuffer);
        }

        var digest = hash.GetHashAndReset();
        var fingerprintHash = BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, sizeof(long)));
        return new RulesFingerprint(
            fingerprintHash,
            rows.Count,
            rows.Count == 0 ? 0 : rows.Max(row => row.RuleVersion));
    }
}
