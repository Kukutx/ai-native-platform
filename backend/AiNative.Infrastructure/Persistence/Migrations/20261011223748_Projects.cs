using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiNative.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                table: "work_items",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "milestone_id",
                table: "work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lead_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lead_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    target_date = table.Column<DateOnly>(type: "date", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_projects_agents_lead_agent_id",
                        column: x => x.lead_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_projects_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_projects_users_lead_user_id",
                        column: x => x.lead_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    responsibility = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_members", x => x.id);
                    table.CheckConstraint("ck_project_members_one_member", "(user_id IS NULL) <> (agent_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_project_members_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_milestones", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_milestones_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_milestone_id",
                table: "work_items",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_project_id_status",
                table: "work_items",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_project_members_agent_id",
                table: "project_members",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_project_id_agent_id",
                table: "project_members",
                columns: new[] { "project_id", "agent_id" },
                unique: true,
                filter: "agent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_project_id_user_id",
                table: "project_members",
                columns: new[] { "project_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_tenant_id",
                table: "project_members",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_user_id",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_milestones_project_id_due_date",
                table: "project_milestones",
                columns: new[] { "project_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_project_milestones_tenant_id",
                table: "project_milestones",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_lead_agent_id",
                table: "projects",
                column: "lead_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_lead_user_id",
                table: "projects",
                column: "lead_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_tenant_id_status",
                table: "projects",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_work_items_project_milestones_milestone_id",
                table: "work_items",
                column: "milestone_id",
                principalTable: "project_milestones",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_work_items_projects_project_id",
                table: "work_items",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // 已有空间的内置角色与智能体补上项目管理权限（与新空间的默认配置一致）。
            migrationBuilder.Sql("""
                UPDATE roles SET features = features || ARRAY['projects.view', 'projects.manage']
                WHERE key = 'member' AND is_system AND NOT features && ARRAY['projects.view'];
                UPDATE roles SET features = features || ARRAY['projects.view']
                WHERE key = 'viewer' AND is_system AND NOT features && ARRAY['projects.view'];
                UPDATE agents SET permissions = permissions || ARRAY['projects.view', 'projects.manage']
                WHERE status <> 'Terminated' AND permissions && ARRAY['agents.manage'] AND NOT permissions && ARRAY['projects.view'];
                UPDATE agents SET permissions = permissions || ARRAY['projects.view']
                WHERE status <> 'Terminated' AND permissions && ARRAY['tasks.view'] AND NOT permissions && ARRAY['projects.view'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_work_items_project_milestones_milestone_id",
                table: "work_items");

            migrationBuilder.DropForeignKey(
                name: "fk_work_items_projects_project_id",
                table: "work_items");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "project_milestones");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropIndex(
                name: "ix_work_items_milestone_id",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "ix_work_items_project_id_status",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "due_date",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "milestone_id",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "work_items");
        }
    }
}
