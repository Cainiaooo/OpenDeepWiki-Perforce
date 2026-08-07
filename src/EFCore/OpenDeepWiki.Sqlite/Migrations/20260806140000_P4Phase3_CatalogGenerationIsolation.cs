using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations
{
    [DbContext(typeof(SqliteDbContext))]
    [Migration("20260806140000_P4Phase3_CatalogGenerationIsolation")]
    public partial class P4Phase3_CatalogGenerationIsolation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenerationId",
                table: "DocCatalogs",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GenerationId",
                table: "DocFiles",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.DropIndex(
                name: "IX_DocCatalogs_BranchLanguageId_Path",
                table: "DocCatalogs");

            migrationBuilder.CreateIndex(
                name: "IX_DocCatalogs_BranchLanguageId_Path_GenerationId",
                table: "DocCatalogs",
                columns: new[] { "BranchLanguageId", "Path", "GenerationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocFiles_BranchLanguageId_GenerationId",
                table: "DocFiles",
                columns: new[] { "BranchLanguageId", "GenerationId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocCatalogs_BranchLanguageId_Path_GenerationId",
                table: "DocCatalogs");

            migrationBuilder.DropIndex(
                name: "IX_DocFiles_BranchLanguageId_GenerationId",
                table: "DocFiles");

            migrationBuilder.DropColumn(name: "GenerationId", table: "DocCatalogs");
            migrationBuilder.DropColumn(name: "GenerationId", table: "DocFiles");

            migrationBuilder.CreateIndex(
                name: "IX_DocCatalogs_BranchLanguageId_Path",
                table: "DocCatalogs",
                columns: new[] { "BranchLanguageId", "Path" },
                unique: true);
        }
    }
}
