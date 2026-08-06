using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    [DbContext(typeof(PostgresqlDbContext))]
    [Migration("20260806160000_P4Phase3_UeKnowledgePackage")]
    public partial class P4Phase3_UeKnowledgePackage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UeKnowledgePackages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    BranchId = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Completeness = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExporterVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectIdentity = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    BuildChangelist = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EngineVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TargetPlatform = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExportSource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PackageDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SemanticDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    FactIndexJson = table.Column<string>(type: "text", nullable: true),
                    PackageRootPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValidationWarningsJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
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
