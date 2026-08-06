using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations
{
    [DbContext(typeof(SqliteDbContext))]
    [Migration("20260806160000_P4Phase3_UeKnowledgePackage")]
    public partial class P4Phase3_UeKnowledgePackage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UeKnowledgePackages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BranchId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Completeness = table.Column<int>(type: "INTEGER", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ExporterVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectIdentity = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BuildChangelist = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EngineVersion = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    TargetPlatform = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ExportSource = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    PackageDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SemanticDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    FactIndexJson = table.Column<string>(type: "TEXT", nullable: true),
                    PackageRootPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ValidationWarningsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ExportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IngestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UeKnowledgePackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UeKnowledgePackages_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UeKnowledgePackages_RepositoryBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "RepositoryBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UeKnowledgePackages_RepositoryId_BranchId_PackageDigest",
                table: "UeKnowledgePackages",
                columns: new[] { "RepositoryId", "BranchId", "PackageDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UeKnowledgePackages_RepositoryId_BranchId_IsCurrent",
                table: "UeKnowledgePackages",
                columns: new[] { "RepositoryId", "BranchId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_UeKnowledgePackages_BranchId_BuildChangelist",
                table: "UeKnowledgePackages",
                columns: new[] { "BranchId", "BuildChangelist" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UeKnowledgePackages");
        }
    }
}
