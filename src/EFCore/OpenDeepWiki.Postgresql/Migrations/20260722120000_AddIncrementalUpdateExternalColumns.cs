using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PostgresqlDbContext))]
    [Migration("20260722120000_AddIncrementalUpdateExternalColumns")]
    public partial class AddIncrementalUpdateExternalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalChangedFiles",
                table: "IncrementalUpdateTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTargetRevision",
                table: "IncrementalUpdateTasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalChangedFiles",
                table: "IncrementalUpdateTasks");

            migrationBuilder.DropColumn(
                name: "ExternalTargetRevision",
                table: "IncrementalUpdateTasks");
        }
    }
}
