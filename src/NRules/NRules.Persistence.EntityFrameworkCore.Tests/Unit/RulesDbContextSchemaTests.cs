using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Entities;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class RulesDbContextSchemaTests
{
    [Fact]
    public void Can_build_DbContext_with_default_schema()
    {
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql("Host=localhost;Database=rules;Username=postgres;Password=postgres")
            .Options;

        using var ctx = new RulesDbContext(options);
        Assert.Equal("rules", ctx.Model.FindEntityType(typeof(RuleEntity))!.GetTableName());
    }

    [Fact]
    public void Maps_rule_properties_to_lowercase_snake_case_columns()
    {
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql("Host=localhost;Database=rules;Username=postgres;Password=postgres")
            .Options;

        using var ctx = new RulesDbContext(options);
        var entityType = ctx.Model.FindEntityType(typeof(RuleEntity))!;
        var table = StoreObjectIdentifier.Table("rules", null);
        var expectedColumns = new Dictionary<string, string>
        {
            [nameof(RuleEntity.Name)] = "name",
            [nameof(RuleEntity.Description)] = "description",
            [nameof(RuleEntity.Priority)] = "priority",
            [nameof(RuleEntity.Repeatability)] = "repeatability",
            [nameof(RuleEntity.Tags)] = "tags",
            [nameof(RuleEntity.Properties)] = "properties",
            [nameof(RuleEntity.RuleDefinition)] = "rule_definition",
            [nameof(RuleEntity.RuleVersion)] = "rule_version",
            [nameof(RuleEntity.CreatedAt)] = "created_at",
            [nameof(RuleEntity.UpdatedAt)] = "updated_at",
            ["xmin"] = "xmin"
        };

        foreach (var (propertyName, columnName) in expectedColumns)
        {
            Assert.Equal(columnName, entityType.FindProperty(propertyName)!.GetColumnName(table));
        }
    }

    [Fact]
    public void Migration_script_preserves_xmin_and_rule_triggers()
    {
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql("Host=localhost;Database=rules;Username=postgres;Password=postgres")
            .Options;

        using var ctx = new RulesDbContext(options);
        var script = ctx.GetService<IMigrator>().GenerateScript();
        var tableMatch = Regex.Match(script, @"(?is)CREATE TABLE rules \((.*?)\);");

        Assert.True(tableMatch.Success, "Expected migration script to create the rules table.");
        Assert.DoesNotContain("xmin", tableMatch.Groups[1].Value);
        foreach (var columnName in new[]
                 {
                     "name", "description", "priority", "repeatability", "tags", "properties",
                     "rule_definition", "rule_version", "created_at", "updated_at"
                 })
        {
            Assert.Matches($@"(?m)^\s*{Regex.Escape(columnName)}\s+", tableMatch.Groups[1].Value);
        }

        Assert.Contains("CREATE OR REPLACE FUNCTION rules_set_version()", script);
        Assert.Contains("NEW.rule_version := OLD.rule_version + 1;", script);
        Assert.Contains("NEW.created_at := OLD.created_at;", script);
        Assert.Contains("NEW.updated_at := now();", script);
        Assert.Contains("NEW.rule_version := 1;", script);
        Assert.Contains("CREATE TRIGGER rules_set_version_trg", script);
        Assert.Contains("CREATE OR REPLACE FUNCTION rules_notify()", script);
        Assert.Contains("pg_notify('rules_changed', OLD.name)", script);
        Assert.Contains("pg_notify('rules_changed', NEW.name)", script);
        Assert.Contains("CREATE TRIGGER rules_changed_trg", script);

        var migrationId = ctx.Database.GetMigrations().Single();
        var downScript = ctx.GetService<IMigrator>().GenerateScript(migrationId, Migration.InitialDatabase);
        var dropVersionTriggerIndex = downScript.IndexOf("DROP TRIGGER IF EXISTS rules_set_version_trg", StringComparison.Ordinal);
        var dropNotifyTriggerIndex = downScript.IndexOf("DROP TRIGGER IF EXISTS rules_changed_trg", StringComparison.Ordinal);
        var dropVersionFunctionIndex = downScript.IndexOf("DROP FUNCTION IF EXISTS rules_set_version()", StringComparison.Ordinal);
        var dropNotifyFunctionIndex = downScript.IndexOf("DROP FUNCTION IF EXISTS rules_notify()", StringComparison.Ordinal);
        var dropTableIndex = downScript.IndexOf("DROP TABLE rules", StringComparison.Ordinal);

        Assert.True(dropVersionTriggerIndex >= 0 && dropVersionTriggerIndex < dropTableIndex);
        Assert.True(dropNotifyTriggerIndex >= 0 && dropNotifyTriggerIndex < dropTableIndex);
        Assert.True(dropVersionFunctionIndex >= 0 && dropVersionFunctionIndex < dropTableIndex);
        Assert.True(dropNotifyFunctionIndex >= 0 && dropNotifyFunctionIndex < dropTableIndex);
    }
}
