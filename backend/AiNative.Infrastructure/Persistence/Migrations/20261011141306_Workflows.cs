using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Workflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "workflow_run_id",
                table: "approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trigger_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    event_filter = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflows_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version = table.Column<int>(type: "integer", nullable: false),
                    workflow_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    trigger_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    trigger_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_step = table.Column<int>(type: "integer", nullable: false),
                    resume_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waiting_approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    waiting_work_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    triggered_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    triggered_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_runs_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_run_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    step_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_json = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_run_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_run_steps_workflow_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_run_steps_run_id_index",
                table: "workflow_run_steps",
                columns: new[] { "run_id", "index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_run_steps_tenant_id",
                table: "workflow_run_steps",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_active",
                table: "workflow_runs",
                columns: new[] { "status", "created_at" },
                filter: "status IN ('Queued', 'Running', 'Waiting')");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_tenant_id_created_at",
                table: "workflow_runs",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_waiting_approval_id",
                table: "workflow_runs",
                column: "waiting_approval_id",
                filter: "waiting_approval_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_waiting_work_item_id",
                table: "workflow_runs",
                column: "waiting_work_item_id",
                filter: "waiting_work_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_workflow_id_created_at",
                table: "workflow_runs",
                columns: new[] { "workflow_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflows_next_run_at",
                table: "workflows",
                column: "next_run_at",
                filter: "is_enabled AND trigger_kind = 'Schedule'");

            migrationBuilder.CreateIndex(
                name: "ix_workflows_tenant_id_event_type",
                table: "workflows",
                columns: new[] { "tenant_id", "event_type" },
                filter: "is_enabled AND trigger_kind = 'Event'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_run_steps");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "workflows");

            migrationBuilder.DropColumn(
                name: "workflow_run_id",
                table: "approvals");
        }
    }
}
