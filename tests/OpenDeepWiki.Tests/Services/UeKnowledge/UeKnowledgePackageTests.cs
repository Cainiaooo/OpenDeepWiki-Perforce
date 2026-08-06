using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.UeKnowledge;
using OpenDeepWiki.Tests.Chat.Config;
using Xunit;

namespace OpenDeepWiki.Tests.Services.UeKnowledge;

public class UeKnowledgePackageTests
{
    [Fact]
    public void Validate_SampleFixture_IsStableAndComplete()
    {
        var root = ResolveFixtureRoot();
        var loader = new UeKnowledgePackageLoader();
        var validator = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder());

        var firstLoad = loader.LoadFromDirectory(root);
        var secondLoad = loader.LoadFromDirectory(root);
        Assert.False(firstLoad.HasErrors, string.Join("; ", firstLoad.Errors));

        var first = validator.Validate(firstLoad, expectedProjectId: "SampleProject", expectedBuildChangelist: "1234567");
        var second = validator.Validate(secondLoad, expectedProjectId: "SampleProject", expectedBuildChangelist: "1234567");

        Assert.True(first.IsValid, string.Join("; ", first.Errors));
        Assert.NotNull(first.Package);
        Assert.NotNull(first.FactIndex);
        Assert.Equal(first.Package!.PackageDigest, second.Package!.PackageDigest);
        Assert.Equal(first.Package.SemanticDigest, second.Package.SemanticDigest);
        Assert.Equal("SampleProject", first.FactIndex!.ProjectId);
        Assert.Equal("1234567", first.FactIndex.BuildChangelist);
        Assert.Contains(first.FactIndex.Classes, c => c.Name == "ASampleCharacter");
        Assert.Contains(first.FactIndex.GameplayTags, t => t.Tag == "Sample.Combat.Melee");
        Assert.Contains(first.FactIndex.McpTools, t => t.ToolName == "ue.spawn_actor");
        Assert.False(first.FactIndex.IsPartial);
    }

    [Fact]
    public void Validate_RejectsDigestMismatchAndProjectIdentityMismatch()
    {
        var root = CreateTempPackage(modifyShard: true);
        try
        {
            var loader = new UeKnowledgePackageLoader();
            var validator = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder());
            var load = loader.LoadFromDirectory(root);

            var badDigest = validator.Validate(load, expectedProjectId: "SampleProject");
            Assert.False(badDigest.IsValid);
            Assert.Contains(badDigest.Errors, e => e.Contains("digest", StringComparison.OrdinalIgnoreCase));

            // restore content digest by rewriting package correctly then mismatch project
            var goodRoot = CreateTempPackage(modifyShard: false);
            try
            {
                var goodLoad = loader.LoadFromDirectory(goodRoot);
                var badProject = validator.Validate(goodLoad, expectedProjectId: "OtherProject");
                Assert.False(badProject.IsValid);
                Assert.Contains(badProject.Errors, e => e.Contains("project identity", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                TryDelete(goodRoot);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Validate_RejectsUnsupportedSchema()
    {
        var root = CreateTempPackage(schemaVersion: "99.0", exporterVersion: "99.0.0");
        try
        {
            var load = new UeKnowledgePackageLoader().LoadFromDirectory(root);
            var result = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder())
                .Validate(load, expectedProjectId: "SampleProject");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("schema", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SemanticDiff_DetectsClassAndTagChanges()
    {
        var baseRoot = CreateTempPackage();
        var changedRoot = CreateTempPackage(addExtraTag: true, renameClass: true);
        try
        {
            var loader = new UeKnowledgePackageLoader();
            var validator = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder());
            var left = validator.Validate(loader.LoadFromDirectory(baseRoot), "SampleProject");
            var right = validator.Validate(loader.LoadFromDirectory(changedRoot), "SampleProject");

            Assert.True(left.IsValid);
            Assert.True(right.IsValid);

            var diff = new UeKnowledgeSemanticDiff().Diff(left.FactIndex!, right.FactIndex!);
            Assert.True(diff.HasSemanticChange);
            Assert.Contains(UeKnowledgeSchema.ShardKinds.GameplayTags, diff.ChangedKinds);
            Assert.Contains("Sample.New.Tag", diff.AddedTags);
            Assert.Contains(UeKnowledgeSchema.ShardKinds.Reflection, diff.ChangedKinds);
            Assert.NotEmpty(diff.AffectedDomainHints);
        }
        finally
        {
            TryDelete(baseRoot);
            TryDelete(changedRoot);
        }
    }

    [Fact]
    public void McpContractChecker_RespectsBuildClRange()
    {
        var root = CreateTempPackage();
        try
        {
            var load = new UeKnowledgePackageLoader().LoadFromDirectory(root);
            var validated = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder())
                .Validate(load, "SampleProject");
            Assert.True(validated.IsValid);

            var checker = new UeKnowledgeMcpContractChecker();
            var ok = checker.Check(validated.FactIndex!, "1500", ["ue.spawn_actor"]);
            Assert.True(ok.IsCompatible);

            var tooOld = checker.Check(validated.FactIndex!, "10", ["ue.spawn_actor"]);
            Assert.False(tooOld.IsCompatible);
            Assert.NotEmpty(tooOld.IncompatibleTools);

            var missing = checker.Check(validated.FactIndex!, "1500", ["ue.missing_tool"]);
            Assert.False(missing.IsCompatible);
            Assert.Contains("ue.missing_tool", missing.MissingTools);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Service_IngestIsIdempotentAndSetsCurrent()
    {
        await using var db = CreateDb();
        var repoId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        db.Repositories.Add(new Repository
        {
            Id = repoId,
            OwnerUserId = "owner-1",
            OrgName = "example",
            RepoName = "SampleProject",
            GitUrl = "p4::example",
            CreatedAt = DateTime.UtcNow
        });
        db.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repoId,
            BranchName = "main",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var root = CreateTempPackage();
        try
        {
            var service = CreateService(db);
            var first = await service.IngestAsync(repoId, new IngestUeKnowledgePackageRequest
            {
                BranchId = branchId,
                PackageRootPath = root,
                ExpectedProjectId = "SampleProject",
                ExpectedBuildChangelist = "1234567",
                SetAsCurrent = true
            });

            var second = await service.IngestAsync(repoId, new IngestUeKnowledgePackageRequest
            {
                BranchId = branchId,
                PackageRootPath = root,
                ExpectedProjectId = "SampleProject",
                SetAsCurrent = true
            });

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.PackageDigest, second.PackageDigest);
            Assert.True(first.IsCurrent);

            var count = await db.UeKnowledgePackages.CountAsync(p => p.RepositoryId == repoId && !p.IsDeleted);
            Assert.Equal(1, count);

            var facts = await service.GetCurrentFactIndexAsync(repoId, branchId);
            Assert.NotNull(facts);
            Assert.Equal("SampleProject", facts!.ProjectId);

            var mcp = await service.CheckMcpCompatibilityAsync(repoId, branchId, "1500");
            Assert.NotNull(mcp);
            Assert.True(mcp!.IsCompatible);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Validate_RejectsEmptyKnownShardStructure()
    {
        var root = CreateTempPackage(emptyReflection: true);
        try
        {
            var load = new UeKnowledgePackageLoader().LoadFromDirectory(root);
            var result = new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder())
                .Validate(load, expectedProjectId: "SampleProject");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("classes", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SemanticDiff_DetectsAssetAndSchemaFieldChanges()
    {
        var left = new UeKnowledgeFactIndex
        {
            SchemaVersion = "1.0",
            PackageDigest = "a",
            SemanticDigest = "left",
            ProjectId = "SampleProject",
            BuildChangelist = "1",
            PrimaryAssets =
            [
                new UePrimaryAssetFact
                {
                    StableId = "PrimaryAsset:SampleItem:Item_Potion",
                    AssetType = "SampleItem",
                    Name = "Item_Potion"
                }
            ],
            DataAssetSchemas =
            [
                new UeDataAssetSchemaFact
                {
                    StableId = "Schema:USampleItemData",
                    TypeName = "USampleItemData",
                    Fields =
                    [
                        new UeSchemaFieldFact { Name = "DisplayName", Type = "FText", IsArray = false },
                        new UeSchemaFieldFact { Name = "StackSize", Type = "int32", IsArray = false }
                    ]
                }
            ]
        };

        var right = new UeKnowledgeFactIndex
        {
            SchemaVersion = "1.0",
            PackageDigest = "b",
            SemanticDigest = "right",
            ProjectId = "SampleProject",
            BuildChangelist = "1",
            PrimaryAssets =
            [
                new UePrimaryAssetFact
                {
                    StableId = "PrimaryAsset:SampleItem:Item_Potion",
                    AssetType = "SampleItemV2",
                    Name = "Item_Potion_Renamed"
                }
            ],
            DataAssetSchemas =
            [
                new UeDataAssetSchemaFact
                {
                    StableId = "Schema:USampleItemData",
                    TypeName = "USampleItemData",
                    Fields =
                    [
                        new UeSchemaFieldFact { Name = "DisplayName", Type = "FText", IsArray = false },
                        new UeSchemaFieldFact { Name = "StackSize", Type = "int64", IsArray = false }
                    ]
                }
            ]
        };

        var diff = new UeKnowledgeSemanticDiff().Diff(left, right);
        Assert.True(diff.HasSemanticChange);
        Assert.Contains(UeKnowledgeSchema.ShardKinds.PrimaryAssets, diff.ChangedKinds);
        Assert.Contains(UeKnowledgeSchema.ShardKinds.DataSchemas, diff.ChangedKinds);
    }

    [Fact]
    public async Task Service_GetFactIndex_IsScopedToRepository()
    {
        await using var db = CreateDb();
        var repoA = Guid.NewGuid().ToString();
        var repoB = Guid.NewGuid().ToString();
        var branchA = Guid.NewGuid().ToString();
        var branchB = Guid.NewGuid().ToString();

        db.Repositories.AddRange(
            new Repository
            {
                Id = repoA,
                OwnerUserId = "owner-1",
                OrgName = "example",
                RepoName = "SampleProject",
                GitUrl = "p4::a",
                CreatedAt = DateTime.UtcNow
            },
            new Repository
            {
                Id = repoB,
                OwnerUserId = "owner-1",
                OrgName = "example",
                RepoName = "OtherProject",
                GitUrl = "p4::b",
                CreatedAt = DateTime.UtcNow
            });
        db.RepositoryBranches.AddRange(
            new RepositoryBranch
            {
                Id = branchA,
                RepositoryId = repoA,
                BranchName = "main",
                CreatedAt = DateTime.UtcNow
            },
            new RepositoryBranch
            {
                Id = branchB,
                RepositoryId = repoB,
                BranchName = "main",
                CreatedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var root = CreateTempPackage();
        try
        {
            var service = CreateService(db);
            var packageA = await service.IngestAsync(repoA, new IngestUeKnowledgePackageRequest
            {
                BranchId = branchA,
                PackageRootPath = root,
                ExpectedProjectId = "SampleProject",
                SetAsCurrent = true
            });

            // 用 packageA 的 ID 查仓库 B：不得泄露
            var leaked = await service.GetFactIndexAsync(repoB, packageA.Id);
            Assert.Null(leaked);

            var owned = await service.GetFactIndexAsync(repoA, packageA.Id);
            Assert.NotNull(owned);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Service_MarkStale_ClearsCurrentVisibility()
    {
        await using var db = CreateDb();
        var repoId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        db.Repositories.Add(new Repository
        {
            Id = repoId,
            OwnerUserId = "owner-1",
            OrgName = "example",
            RepoName = "SampleProject",
            GitUrl = "p4::example",
            CreatedAt = DateTime.UtcNow
        });
        db.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repoId,
            BranchName = "main",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var root = CreateTempPackage();
        try
        {
            var service = CreateService(db);
            var package = await service.IngestAsync(repoId, new IngestUeKnowledgePackageRequest
            {
                BranchId = branchId,
                PackageRootPath = root,
                ExpectedProjectId = "SampleProject",
                ExpectedBuildChangelist = "1234567",
                SetAsCurrent = true
            });

            await service.MarkStaleIfIncompatibleAsync(repoId, branchId, "9999999");

            Assert.Null(await service.GetCurrentAsync(repoId, branchId));
            Assert.Null(await service.GetCurrentFactIndexAsync(repoId, branchId));

            // 历史包仍可按仓库 + packageId 查询
            var historical = await service.GetFactIndexAsync(repoId, package.Id);
            Assert.NotNull(historical);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Service_RejectsCorruptedPackage()
    {
        await using var db = CreateDb();
        var repoId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        db.Repositories.Add(new Repository
        {
            Id = repoId,
            OwnerUserId = "owner-1",
            OrgName = "example",
            RepoName = "SampleProject",
            GitUrl = "p4::example",
            CreatedAt = DateTime.UtcNow
        });
        db.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repoId,
            BranchName = "main",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var root = CreateTempPackage(modifyShard: true);
        try
        {
            var service = CreateService(db);
            var ex = await Assert.ThrowsAsync<UeKnowledgeException>(() => service.IngestAsync(repoId, new IngestUeKnowledgePackageRequest
            {
                BranchId = branchId,
                PackageRootPath = root,
                ExpectedProjectId = "SampleProject"
            }));

            Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
            Assert.Empty(await db.UeKnowledgePackages.ToListAsync());
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static IUeKnowledgePackageService CreateService(TestConfigDbContext db)
        => new UeKnowledgePackageService(
            db,
            new UeKnowledgePackageLoader(),
            new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder()),
            new UeKnowledgeSemanticDiff(),
            new UeKnowledgeMcpContractChecker());

    private static TestConfigDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TestConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestConfigDbContext(options);
    }

    private static string ResolveFixtureRoot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "perforce", "phase3", "fixtures", "ue-knowledge")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "docs", "perforce", "phase3", "fixtures", "ue-knowledge")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "docs", "perforce", "phase3", "fixtures", "ue-knowledge"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        // Fallback: generate an equivalent package
        return CreateTempPackage();
    }

    private static string CreateTempPackage(
        bool modifyShard = false,
        bool addExtraTag = false,
        bool renameClass = false,
        bool emptyReflection = false,
        string schemaVersion = "1.0",
        string exporterVersion = "1.0.0")
    {
        var root = Path.Combine(Path.GetTempPath(), "odw-uek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "reflection"));
        Directory.CreateDirectory(Path.Combine(root, "gameplay"));
        Directory.CreateDirectory(Path.Combine(root, "mcp"));

        var className = renameClass ? "ASampleCharacterV2" : "ASampleCharacter";
        var classesJson = emptyReflection
            ? """{ "classes": [] }"""
            : $$"""
            {
              "classes": [
                {
                  "stableId": "SampleProject.SampleGameplay.{{className}}",
                  "name": "{{className}}",
                  "superClass": "ACharacter",
                  "source": "Native",
                  "moduleName": "SampleGameplay",
                  "path": "SampleProject/Source/SampleGameplay/Public/SampleCharacter.h",
                  "properties": [
                    { "name": "MaxHealth", "type": "float", "category": "Combat", "specifiers": ["EditAnywhere"] }
                  ],
                  "functions": [
                    { "name": "ApplyDamage", "returnType": "void", "category": "Combat", "specifiers": ["BlueprintCallable"], "parameters": ["float Damage"] }
                  ]
                }
              ]
            }
            """;

        var tags = new List<object>
        {
            new { tag = "Sample.Combat.Melee", source = "DefaultGameplayTags.ini", comment = "Melee" }
        };
        if (addExtraTag)
        {
            tags.Add(new { tag = "Sample.New.Tag", source = "DefaultGameplayTags.ini", comment = "New" });
        }

        var tagsJson = JsonSerializer.Serialize(new { tags }, UeKnowledgeDigest.PrettyJsonOptions);
        var mcpJson = """
            {
              "tools": [
                {
                  "toolName": "ue.spawn_actor",
                  "description": "Spawn actor",
                  "inputSchemaJson": "{\"type\":\"object\"}",
                  "preconditions": ["EditorWorldLoaded"],
                  "sideEffects": ["MutatesEditorWorld"],
                  "minBuildChangelist": "1000",
                  "maxBuildChangelist": null
                }
              ]
            }
            """;

        var classesPath = Path.Combine(root, "reflection", "classes-01.json");
        var tagsPath = Path.Combine(root, "gameplay", "tags.json");
        var mcpPath = Path.Combine(root, "mcp", "tool-contracts.json");
        File.WriteAllText(classesPath, classesJson);
        File.WriteAllText(tagsPath, tagsJson);
        File.WriteAllText(mcpPath, mcpJson);

        if (modifyShard)
        {
            // write correct digests first then corrupt file without updating digest
            WriteManifest(root, schemaVersion, exporterVersion, classesPath, tagsPath, mcpPath);
            File.WriteAllText(classesPath, classesJson.Replace("MaxHealth", "MaxHealthCorrupted", StringComparison.Ordinal));
            return root;
        }

        WriteManifest(root, schemaVersion, exporterVersion, classesPath, tagsPath, mcpPath);
        return root;
    }

    private static void WriteManifest(
        string root,
        string schemaVersion,
        string exporterVersion,
        string classesPath,
        string tagsPath,
        string mcpPath)
    {
        var shards = new[]
        {
            new
            {
                name = "reflection/classes",
                relativePath = "reflection/classes-01.json",
                digest = UeKnowledgeDigest.ComputeFileDigest(classesPath),
                kind = "reflection",
                objectCount = 1
            },
            new
            {
                name = "gameplay/tags",
                relativePath = "gameplay/tags.json",
                digest = UeKnowledgeDigest.ComputeFileDigest(tagsPath),
                kind = "gameplay-tags",
                objectCount = 1
            },
            new
            {
                name = "mcp/tool-contracts",
                relativePath = "mcp/tool-contracts.json",
                digest = UeKnowledgeDigest.ComputeFileDigest(mcpPath),
                kind = "mcp-contracts",
                objectCount = 1
            }
        };

        var manifest = new
        {
            schemaVersion,
            exporterVersion,
            project = new
            {
                projectId = "SampleProject",
                projectName = "Sample Project",
                branch = "main"
            },
            buildChangelist = "1234567",
            engineVersion = "5.4.0",
            targetPlatform = "Win64",
            exportSource = "Commandlet",
            exportedAtUtc = "2026-08-06T00:00:00Z",
            completeness = "Complete",
            errors = Array.Empty<string>(),
            truncationNotes = Array.Empty<string>(),
            privacyFilterNotes = new[] { "Instance values excluded" },
            shards
        };

        File.WriteAllText(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(manifest, UeKnowledgeDigest.PrettyJsonOptions));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
