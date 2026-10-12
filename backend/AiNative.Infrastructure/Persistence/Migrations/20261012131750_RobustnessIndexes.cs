using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RobustnessIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_resume",
                table: "workflow_runs",
                column: "resume_at",
                filter: "status = 'Waiting' AND resume_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_description",
                table: "work_items",
                column: "description")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_title",
                table: "work_items",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_runs_resume",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "ix_work_items_description",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "ix_work_items_title",
                table: "work_items");
        }
    }
}
