using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NRules.Persistence.EntityFrameworkCore.Entities;

namespace NRules.Persistence.EntityFrameworkCore;

public class RulesDbContext : DbContext
{
    public RulesDbContext(DbContextOptions<RulesDbContext> options) : base(options)
    {
    }

    public DbSet<RuleEntity> Rules => Set<RuleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RuleEntity>();
        entity.ToTable("rules", table =>
            table.HasCheckConstraint(
                "CK_rules_rule_definition_name_matches_name",
                "rule_definition ->> 'name' = name"));
        entity.HasKey(rule => rule.Name);
        entity.Property(rule => rule.Name)
            .HasColumnName("name")
            .HasColumnType("varchar(200)");

        entity.Property(rule => rule.Description)
            .HasColumnName("description");

        entity.Property(rule => rule.Repeatability)
            .HasColumnName("repeatability")
            .HasConversion<short>()
            .HasColumnType("smallint");
        entity.Property(rule => rule.Priority)
            .HasColumnName("priority")
            .HasDefaultValue(0);

        entity.Property(rule => rule.Properties)
            .HasColumnName("properties")
            .HasColumnType("jsonb")
            .IsRequired();

        entity.Property(rule => rule.RuleDefinition)
            .HasColumnName("rule_definition")
            .HasColumnType("jsonb")
            .IsRequired();

        entity.Property(rule => rule.Tags)
            .HasColumnName("tags")
            .HasColumnType("text[]")
            .HasDefaultValue(Array.Empty<string>())
            .Metadata.SetValueComparer(
                new ValueComparer<string[]>(
                    (left, right) => left!.SequenceEqual(right!),
                    value => value.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode())),
                    value => value.ToArray()));

        entity.Property(rule => rule.RuleVersion)
            .HasColumnName("rule_version")
            .HasDefaultValue(1)
            .ValueGeneratedOnAddOrUpdate();

        entity.Property(rule => rule.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(rule => rule.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
