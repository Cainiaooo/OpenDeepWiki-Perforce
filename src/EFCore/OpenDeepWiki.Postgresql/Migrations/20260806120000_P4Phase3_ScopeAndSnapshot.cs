using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    [DbContext(typeof(PostgresqlDbContext))]
    [Migration("20260806120000_P4Phase3_ScopeAndSnapshot")]
    public partial class P4Phase3_ScopeAndSnapshot : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryScopeConfigurations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ReindexRequired = table.Column<bool>(type: "boolean", nullable: false),
                    IsLegacyFallback = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    ChangeSummary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryScopeConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryScopeConfigurations_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryScopeAuditLogs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PreviousConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    PreviousContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ImpactPreviewJson = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryScopeAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryScopeAuditLogs_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiGenerations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    BranchId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    BranchLanguageId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScopeConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    ScopeContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetRevision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TrackedManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GenerationEngineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SnapshotIdentity = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PublicationIdentity = table.Column<string>(type: "character varying(550)", maxLength: 550, nullable: true),
                    LanguageCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ManifestJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnerTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    OwnerTaskType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiGenerations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiGenerations_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiGenerations_RepositoryBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "RepositoryBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiGenerations_BranchLanguages_BranchLanguageId",
                        column: x => x.BranchLanguageId,
                        principalTable: "BranchLanguages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BranchLanguagePublications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    BranchLanguageId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CurrentGenerationId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    DerivativeSourceGenerationId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchLanguagePublications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchLanguagePublications_BranchLanguages_BranchLanguageId",
                        column: x => x.BranchLanguageId,
                        principalTable: "BranchLanguages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BranchLanguagePublications_WikiGenerations_CurrentGenerationId",
                        column: x => x.CurrentGenerationId,
                        principalTable: "WikiGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SourceWorkspaceLeases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    RepositoryId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceWorkspaceLeases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceWorkspaceLeases_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryScopeConfigurations_RepositoryId_ConfigurationVersion",
                table: "RepositoryScopeConfigurations",
                columns: new[] { "RepositoryId", "ConfigurationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryScopeConfigurations_RepositoryId_IsCurrent",
                table: "RepositoryScopeConfigurations",
                columns: new[] { "RepositoryId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryScopeAuditLogs_RepositoryId_CreatedAt",
                table: "RepositoryScopeAuditLogs",
                columns: new[] { "RepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiGenerations_BranchLanguageId_Status_CreatedAt",
                table: "WikiGenerations",
                columns: new[] { "BranchLanguageId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiGenerations_RepositoryId_BranchId_Status",
                table: "WikiGenerations",
                columns: new[] { "RepositoryId", "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiGenerations_PublicationIdentity",
                table: "WikiGenerations",
                column: "PublicationIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_WikiGenerations_BranchId",
                table: "WikiGenerations",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchLanguagePublications_BranchLanguageId",
                table: "BranchLanguagePublications",
                column: "BranchLanguageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BranchLanguagePublications_CurrentGenerationId",
                table: "BranchLanguagePublications",
                column: "CurrentGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceWorkspaceLeases_RepositoryId",
                table: "SourceWorkspaceLeases",
                column: "RepositoryId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BranchLanguagePublications");
            migrationBuilder.DropTable(name: "RepositoryScopeAuditLogs");
            migrationBuilder.DropTable(name: "RepositoryScopeConfigurations");
            migrationBuilder.DropTable(name: "SourceWorkspaceLeases");
            migrationBuilder.DropTable(name: "WikiGenerations");
        }
    }
}
