using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ErpFulfillmentAndCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "cost_amount",
                table: "erp_stock_movements",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                table: "erp_stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost",
                table: "erp_stock_movements",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "average_cost",
                table: "erp_stock_levels",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "delivered",
                table: "erp_sales_order_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "returned",
                table: "erp_sales_order_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "delivered",
                table: "erp_purchase_order_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "returned",
                table: "erp_purchase_order_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "erp_stock_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sales_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_stock_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_stock_documents_erp_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalTable: "erp_purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_documents_erp_sales_orders_sales_order_id",
                        column: x => x.sales_order_id,
                        principalTable: "erp_sales_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_documents_erp_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "erp_warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_stock_document_lines",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    cost_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cost_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_stock_document_lines", x => new { x.document_id, x.id });
                    table.ForeignKey(
                        name: "fk_erp_stock_document_lines_erp_products_product_id",
                        column: x => x.product_id,
                        principalTable: "erp_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_document_lines_erp_stock_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "erp_stock_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_document_id",
                table: "erp_stock_movements",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_document_lines_product_id",
                table: "erp_stock_document_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_documents_purchase_order_id",
                table: "erp_stock_documents",
                column: "purchase_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_documents_sales_order_id",
                table: "erp_stock_documents",
                column: "sales_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_documents_tenant_id_kind_created_at",
                table: "erp_stock_documents",
                columns: new[] { "tenant_id", "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_documents_tenant_id_number",
                table: "erp_stock_documents",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_documents_warehouse_id",
                table: "erp_stock_documents",
                column: "warehouse_id");

            migrationBuilder.AddForeignKey(
                name: "fk_erp_stock_movements_erp_stock_documents_document_id",
                table: "erp_stock_movements",
                column: "document_id",
                principalTable: "erp_stock_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // 已有数据：已发货、已到货的订单整单记为发完、收完，并各补一张发货单或收货单，经营分析和毛利沿用原来的发货、到货时间。
            // 历史数据没有成本记录，库存、流水和单据的成本一律按产品的参考采购价估算。
            migrationBuilder.Sql("""
                UPDATE erp_sales_order_lines l SET delivered = l.quantity
                FROM erp_sales_orders o WHERE l.order_id = o.id AND o.status = 'Shipped';

                UPDATE erp_purchase_order_lines l SET delivered = l.quantity
                FROM erp_purchase_orders o WHERE l.order_id = o.id AND o.status = 'Received';

                UPDATE erp_stock_levels s SET average_cost = COALESCE(p.cost, 0)
                FROM erp_products p WHERE s.product_id = p.id;

                UPDATE erp_stock_movements m SET unit_cost = COALESCE(p.cost, 0), cost_amount = round(m.quantity * COALESCE(p.cost, 0), 2)
                FROM erp_products p WHERE m.product_id = p.id;

                INSERT INTO erp_stock_documents (id, tenant_id, number, kind, sales_order_id, warehouse_id, currency, amount, note, created_by_id, created_by_kind, created_at, updated_at)
                SELECT gen_random_uuid(), o.tenant_id,
                       'SH-' || CASE WHEN o.n < 10000 THEN lpad(o.n::text, 4, '0') ELSE o.n::text END,
                       'Shipment', o.id, o.warehouse_id, o.currency, o.total, '',
                       COALESCE(m.actor_id, o.created_by_id), COALESCE(m.actor_kind, o.created_by_kind), o.shipped_at, o.shipped_at
                FROM (
                    SELECT *, row_number() OVER (PARTITION BY tenant_id ORDER BY shipped_at, id) AS n
                    FROM erp_sales_orders WHERE status = 'Shipped' AND shipped_at IS NOT NULL
                ) o
                LEFT JOIN LATERAL (
                    SELECT actor_id, actor_kind FROM erp_stock_movements
                    WHERE sales_order_id = o.id AND kind = 'Sale' ORDER BY created_at LIMIT 1
                ) m ON TRUE;

                INSERT INTO erp_stock_documents (id, tenant_id, number, kind, purchase_order_id, warehouse_id, currency, amount, note, created_by_id, created_by_kind, created_at, updated_at)
                SELECT gen_random_uuid(), o.tenant_id,
                       'GR-' || CASE WHEN o.n < 10000 THEN lpad(o.n::text, 4, '0') ELSE o.n::text END,
                       'Receipt', o.id, o.warehouse_id, o.currency, o.total, '',
                       COALESCE(m.actor_id, o.created_by_id), COALESCE(m.actor_kind, o.created_by_kind), o.received_at, o.received_at
                FROM (
                    SELECT *, row_number() OVER (PARTITION BY tenant_id ORDER BY received_at, id) AS n
                    FROM erp_purchase_orders WHERE status = 'Received' AND received_at IS NOT NULL
                ) o
                LEFT JOIN LATERAL (
                    SELECT actor_id, actor_kind FROM erp_stock_movements
                    WHERE purchase_order_id = o.id AND kind = 'Purchase' ORDER BY created_at LIMIT 1
                ) m ON TRUE;

                INSERT INTO erp_stock_document_lines (document_id, product_id, quantity, unit_price, amount, unit_cost, cost_amount, cost_currency)
                SELECT d.id, l.product_id, l.quantity, l.unit_price, l.amount, COALESCE(p.cost, 0), round(l.quantity * COALESCE(p.cost, 0), 2), p.currency
                FROM erp_stock_documents d
                JOIN (
                    SELECT id, order_id, product_id, quantity, unit_price, amount, 'Shipment' AS kind FROM erp_sales_order_lines
                    UNION ALL
                    SELECT id, order_id, product_id, quantity, unit_price, amount, 'Receipt' FROM erp_purchase_order_lines
                ) l ON l.kind = d.kind AND l.order_id = COALESCE(d.sales_order_id, d.purchase_order_id)
                JOIN erp_products p ON p.id = l.product_id
                ORDER BY d.id, l.id;

                UPDATE erp_stock_movements m SET document_id = d.id
                FROM erp_stock_documents d WHERE d.kind = 'Shipment' AND m.kind = 'Sale' AND m.sales_order_id = d.sales_order_id;

                UPDATE erp_stock_movements m SET document_id = d.id
                FROM erp_stock_documents d WHERE d.kind = 'Receipt' AND m.kind = 'Purchase' AND m.purchase_order_id = d.purchase_order_id;

                INSERT INTO tenant_sequences (tenant_id, name, value)
                SELECT tenant_id, CASE kind WHEN 'Shipment' THEN 'erp.sh' ELSE 'erp.gr' END, count(*)
                FROM erp_stock_documents GROUP BY tenant_id, kind
                ON CONFLICT (tenant_id, name) DO UPDATE SET value = GREATEST(tenant_sequences.value, EXCLUDED.value);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_erp_stock_movements_erp_stock_documents_document_id",
                table: "erp_stock_movements");

            migrationBuilder.DropTable(
                name: "erp_stock_document_lines");

            migrationBuilder.DropTable(
                name: "erp_stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_erp_stock_movements_document_id",
                table: "erp_stock_movements");

            migrationBuilder.DropColumn(
                name: "cost_amount",
                table: "erp_stock_movements");

            migrationBuilder.DropColumn(
                name: "document_id",
                table: "erp_stock_movements");

            migrationBuilder.DropColumn(
                name: "unit_cost",
                table: "erp_stock_movements");

            migrationBuilder.DropColumn(
                name: "average_cost",
                table: "erp_stock_levels");

            migrationBuilder.DropColumn(
                name: "delivered",
                table: "erp_sales_order_lines");

            migrationBuilder.DropColumn(
                name: "returned",
                table: "erp_sales_order_lines");

            migrationBuilder.DropColumn(
                name: "delivered",
                table: "erp_purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "returned",
                table: "erp_purchase_order_lines");
        }
    }
}
