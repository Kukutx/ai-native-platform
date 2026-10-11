using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Erp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    track_inventory = table.Column<bool>(type: "boolean", nullable: false),
                    reorder_point = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_products_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_warehouses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_warehouses", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_warehouses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_sequences", x => new { x.tenant_id, x.name });
                    table.ForeignKey(
                        name: "fk_tenant_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_purchase_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_date = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ordered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_purchase_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_purchase_orders_crm_companies_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "crm_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_purchase_orders_erp_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "erp_warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_purchase_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_sales_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_sales_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_crm_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "crm_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_crm_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "crm_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_crm_deals_deal_id",
                        column: x => x.deal_id,
                        principalTable: "crm_deals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_erp_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "erp_warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_erp_sales_orders_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "erp_stock_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    reserved = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_stock_levels", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_stock_levels_erp_products_product_id",
                        column: x => x.product_id,
                        principalTable: "erp_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_erp_stock_levels_erp_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "erp_warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_erp_stock_levels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_purchase_order_lines",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_purchase_order_lines", x => new { x.order_id, x.id });
                    table.ForeignKey(
                        name: "fk_erp_purchase_order_lines_erp_products_product_id",
                        column: x => x.product_id,
                        principalTable: "erp_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_purchase_order_lines_erp_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "erp_purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_sales_order_lines",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_sales_order_lines", x => new { x.order_id, x.id });
                    table.ForeignKey(
                        name: "fk_erp_sales_order_lines_erp_products_product_id",
                        column: x => x.product_id,
                        principalTable: "erp_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_sales_order_lines_erp_sales_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "erp_sales_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "erp_stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sales_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_stock_movements", x => x.id);
                    table.ForeignKey(
                        name: "fk_erp_stock_movements_erp_products_product_id",
                        column: x => x.product_id,
                        principalTable: "erp_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_movements_erp_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalTable: "erp_purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_stock_movements_erp_sales_orders_sales_order_id",
                        column: x => x.sales_order_id,
                        principalTable: "erp_sales_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_erp_stock_movements_erp_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "erp_warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_erp_stock_movements_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erp_products_name",
                table: "erp_products",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_products_sku",
                table: "erp_products",
                column: "sku")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_products_tenant_id_category",
                table: "erp_products",
                columns: new[] { "tenant_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_products_tenant_id_sku",
                table: "erp_products",
                columns: new[] { "tenant_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_order_lines_product_id",
                table: "erp_purchase_order_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_supplier_id",
                table: "erp_purchase_orders",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_tenant_id_created_at",
                table: "erp_purchase_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_tenant_id_number",
                table: "erp_purchase_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_tenant_id_status_created_at",
                table: "erp_purchase_orders",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_purchase_orders_warehouse_id",
                table: "erp_purchase_orders",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_order_lines_product_id",
                table: "erp_sales_order_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_company_id",
                table: "erp_sales_orders",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_contact_id",
                table: "erp_sales_orders",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_deal_id",
                table: "erp_sales_orders",
                column: "deal_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_owner_user_id",
                table: "erp_sales_orders",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_tenant_id_created_at",
                table: "erp_sales_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_tenant_id_number",
                table: "erp_sales_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_tenant_id_owner_user_id",
                table: "erp_sales_orders",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_tenant_id_status_created_at",
                table: "erp_sales_orders",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_sales_orders_warehouse_id",
                table: "erp_sales_orders",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_levels_product_id_warehouse_id",
                table: "erp_stock_levels",
                columns: new[] { "product_id", "warehouse_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_levels_tenant_id_warehouse_id",
                table: "erp_stock_levels",
                columns: new[] { "tenant_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_levels_warehouse_id",
                table: "erp_stock_levels",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_product_id_created_at",
                table: "erp_stock_movements",
                columns: new[] { "product_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_purchase_order_id",
                table: "erp_stock_movements",
                column: "purchase_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_sales_order_id",
                table: "erp_stock_movements",
                column: "sales_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_tenant_id_created_at",
                table: "erp_stock_movements",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_stock_movements_warehouse_id_created_at",
                table: "erp_stock_movements",
                columns: new[] { "warehouse_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_warehouses_default",
                table: "erp_warehouses",
                column: "tenant_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_erp_warehouses_tenant_id_name",
                table: "erp_warehouses",
                columns: new[] { "tenant_id", "name" });

            // 已有空间补一个默认仓库；内置角色与首席助理获得与新空间相同的进销存权限。
            migrationBuilder.Sql("""
                INSERT INTO erp_warehouses (id, tenant_id, name, address, is_default, created_at, updated_at)
                SELECT gen_random_uuid(), t.id, '默认仓库', '', true, now(), now()
                FROM tenants t
                WHERE NOT EXISTS (SELECT 1 FROM erp_warehouses w WHERE w.tenant_id = t.id);
                UPDATE roles SET features = features || ARRAY['erp.view', 'erp.sales']
                WHERE key = 'member' AND is_system AND NOT features && ARRAY['erp.view'];
                UPDATE roles SET features = features || ARRAY['erp.view']
                WHERE key = 'viewer' AND is_system AND NOT features && ARRAY['erp.view'];
                UPDATE agents SET permissions = permissions || ARRAY['erp.view', 'erp.sales', 'erp.purchase']
                WHERE status <> 'Terminated' AND permissions && ARRAY['agents.manage'] AND NOT permissions && ARRAY['erp.view'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erp_purchase_order_lines");

            migrationBuilder.DropTable(
                name: "erp_sales_order_lines");

            migrationBuilder.DropTable(
                name: "erp_stock_levels");

            migrationBuilder.DropTable(
                name: "erp_stock_movements");

            migrationBuilder.DropTable(
                name: "tenant_sequences");

            migrationBuilder.DropTable(
                name: "erp_products");

            migrationBuilder.DropTable(
                name: "erp_purchase_orders");

            migrationBuilder.DropTable(
                name: "erp_sales_orders");

            migrationBuilder.DropTable(
                name: "erp_warehouses");
        }
    }
}
