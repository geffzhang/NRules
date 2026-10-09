using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NRules.Persistence.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class EnforceRuleDefinitionNameMatchesName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_rules_rule_definition_name_matches_name",
                table: "rules",
                sql: "rule_definition ->> 'name' = name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_rules_rule_definition_name_matches_name",
                table: "rules");
        }
    }
}
