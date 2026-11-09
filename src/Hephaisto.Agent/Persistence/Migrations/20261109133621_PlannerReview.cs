using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlannerReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planner_review_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    slot = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    problem = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    open = table.Column<int>(type: "integer", nullable: false),
                    reviewed = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    kept = table.Column<int>(type: "integer", nullable: false),
                    jobs = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false),
                    count_close = table.Column<int>(type: "integer", nullable: false),
                    count_bug = table.Column<int>(type: "integer", nullable: false),
                    count_feature = table.Column<int>(type: "integer", nullable: false),
                    count_unsure = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_planner_review_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "planner_review_verdicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    on_board = table.Column<bool>(type: "boolean", nullable: false),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    group = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    close_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    related = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: true),
                    confidence = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    priority = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    issue_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_group = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    decision_close_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    decision_agreed = table.Column<bool>(type: "boolean", nullable: false),
                    decision_unsure = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_planner_review_verdicts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planner_review_runs_started_at",
                table: "planner_review_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_planner_review_runs_state",
                table: "planner_review_runs",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ux_planner_review_runs_slot",
                table: "planner_review_runs",
                column: "slot",
                unique: true,
                filter: "slot IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_planner_review_verdicts_issue",
                table: "planner_review_verdicts",
                columns: new[] { "repository", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planner_review_runs");

            migrationBuilder.DropTable(
                name: "planner_review_verdicts");
        }
    }
}
