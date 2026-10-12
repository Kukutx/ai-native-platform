using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiInstanceCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "executor_id",
                table: "workflow_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "interruption_count",
                table: "workflow_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "executor_id",
                table: "heartbeat_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "executor_id",
                table: "approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cluster_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cluster_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cluster_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_unrecorded",
                table: "approvals",
                column: "decided_at",
                filter: "status = 'Approved' AND result_json IS NULL AND execution_error IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cluster_instances_last_seen_at",
                table: "cluster_instances",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_cluster_messages_created_at",
                table: "cluster_messages",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_instances");

            migrationBuilder.DropTable(
                name: "cluster_messages");

            migrationBuilder.DropIndex(
                name: "ix_approvals_unrecorded",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "executor_id",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "interruption_count",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "executor_id",
                table: "heartbeat_runs");

            migrationBuilder.DropColumn(
                name: "executor_id",
                table: "approvals");
        }
    }
}
