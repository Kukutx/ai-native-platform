using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.CreateTable(
                name: "analytics_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    breakdown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    range = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    chart = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analytics_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_analytics_reports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_tenant_id_completed_at",
                table: "work_items",
                columns: new[] { "tenant_id", "completed_at" },
                filter: "completed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_tenant_id_created_at",
                table: "work_items",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_tenant_id_shipped_at",
                table: "erp_sales_orders",
                columns: new[] { "tenant_id", "shipped_at" },
                filter: "status = 'Shipped'");

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_tenant_id_received_at",
                table: "erp_purchase_orders",
                columns: new[] { "tenant_id", "received_at" },
                filter: "status = 'Received'");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_tenant_id_created_at",
                table: "crm_deals",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_analytics_reports_tenant_id_position",
                table: "analytics_reports",
                columns: new[] { "tenant_id", "position" });

            // 已有空间的内置角色与智能体补上经营分析权限（与新空间的默认配置一致）。
            migrationBuilder.Sql("""
                UPDATE roles SET features = features || ARRAY['analytics.view', 'analytics.manage']
                WHERE key = 'member' AND is_system AND NOT features && ARRAY['analytics.view'];
                UPDATE roles SET features = features || ARRAY['analytics.view']
                WHERE key = 'viewer' AND is_system AND NOT features && ARRAY['analytics.view'];
                UPDATE agents SET permissions = permissions || ARRAY['analytics.view', 'analytics.manage']
                WHERE status <> 'Terminated' AND permissions && ARRAY['agents.manage'] AND NOT permissions && ARRAY['analytics.view'];
                UPDATE agents SET permissions = permissions || ARRAY['analytics.view']
                WHERE status <> 'Terminated' AND permissions && ARRAY['tasks.view'] AND NOT permissions && ARRAY['analytics.view'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_reports");

            migrationBuilder.DropIndex(
                name: "ix_work_items_tenant_id_completed_at",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "ix_work_items_tenant_id_created_at",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "ix_erp_sales_orders_tenant_id_shipped_at",
                table: "erp_sales_orders");

            migrationBuilder.DropIndex(
                name: "ix_erp_purchase_orders_tenant_id_received_at",
                table: "erp_purchase_orders");

            migrationBuilder.DropIndex(
                name: "ix_crm_deals_tenant_id_created_at",
                table: "crm_deals");

            migrationBuilder.DropColumn(
                name: "time_zone",
                table: "tenants");
        }
    }
}
