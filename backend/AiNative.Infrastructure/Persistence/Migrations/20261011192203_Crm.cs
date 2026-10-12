using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Crm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "crm_companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    website = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_companies", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_companies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_companies_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "crm_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    probability = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_stages", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_stages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_contacts_crm_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "crm_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_contacts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_contacts_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "crm_deals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    expected_close_date = table.Column<DateOnly>(type: "date", nullable: true),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_deals", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_deals_crm_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "crm_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_deals_crm_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "crm_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_deals_crm_stages_stage_id",
                        column: x => x.stage_id,
                        principalTable: "crm_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_deals_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "crm_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_activities", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_activities_crm_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "crm_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_activities_crm_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "crm_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_activities_crm_deals_deal_id",
                        column: x => x.deal_id,
                        principalTable: "crm_deals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_crm_activities_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_company_id_occurred_at",
                table: "crm_activities",
                columns: new[] { "company_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_contact_id_occurred_at",
                table: "crm_activities",
                columns: new[] { "contact_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_deal_id_occurred_at",
                table: "crm_activities",
                columns: new[] { "deal_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_tenant_id_occurred_at",
                table: "crm_activities",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_companies_name",
                table: "crm_companies",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_companies_owner_user_id",
                table: "crm_companies",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_companies_tags",
                table: "crm_companies",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_crm_companies_tenant_id_owner_user_id",
                table: "crm_companies",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_companies_tenant_id_updated_at",
                table: "crm_companies",
                columns: new[] { "tenant_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_company_id",
                table: "crm_contacts",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_email",
                table: "crm_contacts",
                column: "email")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_name",
                table: "crm_contacts",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_owner_user_id",
                table: "crm_contacts",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_tags",
                table: "crm_contacts",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_tenant_id_owner_user_id",
                table: "crm_contacts",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_tenant_id_updated_at",
                table: "crm_contacts",
                columns: new[] { "tenant_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_company_id",
                table: "crm_deals",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_contact_id",
                table: "crm_deals",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_owner_user_id",
                table: "crm_deals",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_stage_id",
                table: "crm_deals",
                column: "stage_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_tenant_id_closed_at",
                table: "crm_deals",
                columns: new[] { "tenant_id", "closed_at" },
                filter: "status <> 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_tenant_id_owner_user_id",
                table: "crm_deals",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_tenant_id_status_stage_id",
                table: "crm_deals",
                columns: new[] { "tenant_id", "status", "stage_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_title",
                table: "crm_deals",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_stages_tenant_id_position",
                table: "crm_stages",
                columns: new[] { "tenant_id", "position" });

            // 已有空间：补上默认销售阶段（与 CrmDefaultStages 一致），内置"成员""只读"角色和首席助理补上客户管理权限（与 DefaultRoles、注册流程一致）。
            migrationBuilder.Sql("""
                INSERT INTO crm_stages (id, tenant_id, name, position, probability, kind, created_at, updated_at)
                SELECT gen_random_uuid(), t.id, s.name, s.position, s.probability, s.kind, now(), now()
                FROM tenants t
                CROSS JOIN (VALUES
                    ('线索', 0, 10, 'Open'), ('需求确认', 1, 25, 'Open'), ('方案报价', 2, 50, 'Open'),
                    ('商务谈判', 3, 75, 'Open'), ('赢单', 4, 100, 'Won'), ('输单', 5, 0, 'Lost')
                ) AS s(name, position, probability, kind)
                WHERE NOT EXISTS (SELECT 1 FROM crm_stages c WHERE c.tenant_id = t.id);
                UPDATE roles SET features = features || ARRAY['crm.view', 'crm.manage']
                WHERE key = 'member' AND is_system AND NOT features && ARRAY['crm.view'];
                UPDATE roles SET features = features || ARRAY['crm.view']
                WHERE key = 'viewer' AND is_system AND NOT features && ARRAY['crm.view'];
                UPDATE agents SET permissions = permissions || ARRAY['crm.view', 'crm.manage']
                WHERE status <> 'Terminated' AND permissions && ARRAY['agents.manage'] AND NOT permissions && ARRAY['crm.view'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_activities");

            migrationBuilder.DropTable(
                name: "crm_deals");

            migrationBuilder.DropTable(
                name: "crm_contacts");

            migrationBuilder.DropTable(
                name: "crm_stages");

            migrationBuilder.DropTable(
                name: "crm_companies");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
