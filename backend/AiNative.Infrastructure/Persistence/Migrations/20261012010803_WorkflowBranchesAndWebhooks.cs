using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowBranchesAndWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_run_steps_run_id_index",
                table: "workflow_run_steps");

            migrationBuilder.AddColumn<string>(
                name: "webhook_token",
                table: "workflows",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "path",
                table: "workflow_run_steps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // 已有的运行记录都是顶层步骤，路径就是序号。
            migrationBuilder.Sql("UPDATE workflow_run_steps SET path = index::text;");

            migrationBuilder.CreateIndex(
                name: "ix_workflows_webhook_token",
                table: "workflows",
                column: "webhook_token",
                unique: true,
                filter: "webhook_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_run_steps_run_id_path",
                table: "workflow_run_steps",
                columns: new[] { "run_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflows_webhook_token",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "ix_workflow_run_steps_run_id_path",
                table: "workflow_run_steps");

            migrationBuilder.DropColumn(
                name: "webhook_token",
                table: "workflows");

            // 回退后每个顶层步骤只能有一条记录，分支和遍历里的子步骤记录无法保留。
            migrationBuilder.Sql("DELETE FROM workflow_run_steps WHERE path LIKE '%.%';");

            migrationBuilder.DropColumn(
                name: "path",
                table: "workflow_run_steps");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_run_steps_run_id_index",
                table: "workflow_run_steps",
                columns: new[] { "run_id", "index" },
                unique: true);
        }
    }
}
