using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NRules.Persistence.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class InitRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    name = table.Column<string>(type: "varchar(200)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    repeatability = table.Column<short>(type: "smallint", nullable: false),
                    tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    properties = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    rule_definition = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rules", x => x.name);
                });

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION rules_set_version() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW.rule_version := 1;
                    ELSE
                        NEW.rule_version := OLD.rule_version + 1;
                        NEW.created_at := OLD.created_at;
                    END IF;
                    NEW.updated_at := now();
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER rules_set_version_trg
                BEFORE INSERT OR UPDATE ON rules
                FOR EACH ROW EXECUTE FUNCTION rules_set_version();

                CREATE OR REPLACE FUNCTION rules_notify() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        PERFORM pg_notify('rules_changed', OLD.name);
                        RETURN OLD;
                    END IF;
                    PERFORM pg_notify('rules_changed', NEW.name);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER rules_changed_trg
                AFTER INSERT OR UPDATE OR DELETE ON rules
                FOR EACH ROW EXECUTE FUNCTION rules_notify();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS rules_set_version_trg ON rules;
                DROP TRIGGER IF EXISTS rules_changed_trg ON rules;
                DROP FUNCTION IF EXISTS rules_set_version();
                DROP FUNCTION IF EXISTS rules_notify();
                """);

            migrationBuilder.DropTable(
                name: "rules");
        }
    }
}
