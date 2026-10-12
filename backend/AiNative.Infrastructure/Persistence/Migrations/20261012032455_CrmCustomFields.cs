using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "crm_deals",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "crm_contacts",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "crm_companies",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateTable(
                name: "crm_field_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_field_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_field_definitions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_field_definitions_tenant_id_record_type_key",
                table: "crm_field_definitions",
                columns: new[] { "tenant_id", "record_type", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_field_definitions");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "crm_deals");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "crm_companies");
        }
    }
}
