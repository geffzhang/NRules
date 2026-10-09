using System.Text.Json;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore.Entities;

/// <summary>
/// EF Core entity mapping to the Postgres <c>rules</c> table.
/// </summary>
public class RuleEntity
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public int Priority { get; set; }
    public RuleRepeatability Repeatability { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>JSONB column: PropertyMap of the rule.</summary>
    public JsonDocument Properties { get; set; } = default!;

    /// <summary>JSONB column: full IRuleDefinition serialized with NRules.Json.</summary>
    public JsonDocument RuleDefinition { get; set; } = default!;

    public int RuleVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
