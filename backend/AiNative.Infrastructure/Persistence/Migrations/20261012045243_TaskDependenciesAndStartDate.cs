using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskDependenciesAndStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "start_date",
                table: "work_items",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_item_dependencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    depends_on_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_item_dependencies", x => x.id);
                    table.CheckConstraint("ck_work_item_dependencies_not_self", "work_item_id <> depends_on_id");
                    table.ForeignKey(
                        name: "fk_work_item_dependencies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_item_dependencies_work_items_depends_on_id",
                        column: x => x.depends_on_id,
                        principalTable: "work_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_item_dependencies_work_items_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_item_dependencies_depends_on_id",
                table: "work_item_dependencies",
                column: "depends_on_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_item_dependencies_tenant_id",
                table: "work_item_dependencies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_item_dependencies_work_item_id_depends_on_id",
                table: "work_item_dependencies",
                columns: new[] { "work_item_id", "depends_on_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_item_dependencies");

            migrationBuilder.DropColumn(
                name: "start_date",
                table: "work_items");
        }
    }
}
