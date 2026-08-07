using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    [DbContext(typeof(PostgresqlDbContext))]
    [Migration("20260807120000_P4Phase3_IncrementalImpactPlan")]
    public partial class P4Phase3_IncrementalImpactPlan : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImpactPlanJson",
                table: "IncrementalUpdateTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImpactPlanJson",
                table: "BranchGenerationTasks",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImpactPlanJson",
                table: "IncrementalUpdateTasks");

            migrationBuilder.DropColumn(
                name: "ImpactPlanJson",
                table: "BranchGenerationTasks");
        }
    }
}
