using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using OpenDeepWiki.Infrastructure;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Sqlite;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Impact;

public class IncrementalImpactMigrationTests
{
    private const string MigrationId = "20260807120000_P4Phase3_IncrementalImpactPlan";

    [Fact]
    public async Task SqliteMigration_DeclaresImpactPlanColumns()
    {
        var options = new DbContextOptionsBuilder<SqliteDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new SqliteDbContext(options);

        AssertImpactPlanMigration(context, "TEXT");
    }

    [Fact]
    public async Task SqliteRuntimeMigration_AddsImpactPlanColumnsToExistingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteDbContext(options);
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE IncrementalUpdateTasks DROP COLUMN ImpactPlanJson");
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE BranchGenerationTasks DROP COLUMN ImpactPlanJson");

        Assert.DoesNotContain("ImpactPlanJson", await ReadColumnNamesAsync(connection, "IncrementalUpdateTasks"));
        Assert.DoesNotContain("ImpactPlanJson", await ReadColumnNamesAsync(connection, "BranchGenerationTasks"));

        await DbInitializer.MigrateSqliteAsync(context);
        await DbInitializer.MigrateSqliteAsync(context);

        Assert.Contains("ImpactPlanJson", await ReadColumnNamesAsync(connection, "IncrementalUpdateTasks"));
        Assert.Contains("ImpactPlanJson", await ReadColumnNamesAsync(connection, "BranchGenerationTasks"));
    }

    [Fact]
    public async Task PostgresqlMigration_IsDiscoverable()
    {
        var options = new DbContextOptionsBuilder<PostgresqlDbContext>()
            .UseNpgsql("Host=localhost;Database=opendeepwiki_migration_discovery;Username=test;Password=test")
            .Options;
        await using var context = new PostgresqlDbContext(options);

        AssertImpactPlanMigration(context, "text");
    }

    private static void AssertImpactPlanMigration(DbContext context, string expectedColumnType)
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var migrationType = migrationsAssembly.Migrations[MigrationId];
        var migration = migrationsAssembly.CreateMigration(
            migrationType,
            context.Database.ProviderName!);
        var operations = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Name == "ImpactPlanJson")
            .ToArray();

        Assert.Contains(operations, operation =>
            operation.Table == "IncrementalUpdateTasks"
            && operation.ColumnType == expectedColumnType
            && operation.IsNullable);
        Assert.Contains(operations, operation =>
            operation.Table == "BranchGenerationTasks"
            && operation.ColumnType == expectedColumnType
            && operation.IsNullable);
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }
}
