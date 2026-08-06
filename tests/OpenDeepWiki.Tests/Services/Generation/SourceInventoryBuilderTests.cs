using OpenDeepWiki.Services.Generation;
using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Generation;

public class SourceInventoryBuilderTests
{
    [Fact]
    public void Build_ProducesStableHashAndDiscoversUeStructure()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var request = CreateRequest(root);
            var builder = new SourceInventoryBuilder();

            var first = builder.Build(request);
            var second = builder.Build(request);

            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.Equal(
                first.Files.Select(file => file.RelativePath),
                second.Files.Select(file => file.RelativePath));

            Assert.Contains(first.Projects, project => project.Name == "SampleProject");
            Assert.Contains(first.Plugins, plugin => plugin.Name == "SamplePlugin");
            Assert.Contains(first.Modules, module => module.Name == "SampleGameplay");
            Assert.Contains(first.Modules, module => module.Name == "SamplePlugin");
            Assert.Contains(first.Targets, target => target.Name == "SampleProjectEditor");

            var gameplay = first.Modules.Single(module => module.Name == "SampleGameplay");
            Assert.Contains("Core", gameplay.PublicDependencies);
            Assert.True(gameplay.HasPublic);
            Assert.True(gameplay.HasPrivate);
            Assert.True(gameplay.FileCount >= 2);

            // Intermediate/Binaries under plugins should be pruned by scope excludes
            Assert.DoesNotContain(
                first.Files,
                file => file.RelativePath.Contains("/Binaries/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                first.Files,
                file => file.RelativePath.Contains("/Intermediate/", StringComparison.OrdinalIgnoreCase));

            // Context-only Engine path is not a document candidate
            Assert.DoesNotContain(
                first.Files,
                file => file.RelativePath.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Build_WithTrackedAllowList_DoesNotScanDiskOutsideList()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var request = CreateRequest(root);
            request = new GenerationRequest
            {
                Snapshot = request.Snapshot,
                ResolvedScopes = request.ResolvedScopes,
                Policy = request.Policy,
                WorkingDirectory = request.WorkingDirectory,
                RepositoryId = request.RepositoryId,
                BranchId = request.BranchId,
                BranchLanguageId = request.BranchLanguageId,
                LanguageCode = request.LanguageCode,
                AllowedTrackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "SampleProject/Source/SampleGameplay/SampleGameplay.Build.cs",
                    "SampleProject/Source/SampleGameplay/Public/SampleGameplay.h"
                }
            };

            var inventory = new SourceInventoryBuilder().Build(request);

            Assert.Equal(2, inventory.Files.Count);
            Assert.Single(inventory.Modules);
            Assert.Equal("SampleGameplay", inventory.Modules[0].Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Build_WithEmptyTrackedAllowList_DoesNotFallBackToDiskScan()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var request = CreateRequest(root);
            request = new GenerationRequest
            {
                Snapshot = request.Snapshot,
                ResolvedScopes = request.ResolvedScopes,
                Policy = request.Policy,
                WorkingDirectory = request.WorkingDirectory,
                RepositoryId = request.RepositoryId,
                BranchId = request.BranchId,
                BranchLanguageId = request.BranchLanguageId,
                LanguageCode = request.LanguageCode,
                AllowedTrackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };

            var inventory = new SourceInventoryBuilder().Build(request);

            Assert.Empty(inventory.Files);
            Assert.Empty(inventory.Modules);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Build_FullContentDigest_ChangesWhenTailChanges()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var relative = "SampleProject/Source/SampleGameplay/Private/LargeBlob.cpp";
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var prefix = new string('A', 70 * 1024);
            File.WriteAllText(full, prefix + "TAIL-V1");

            var request = CreateRequest(root);
            request = new GenerationRequest
            {
                Snapshot = request.Snapshot,
                ResolvedScopes = request.ResolvedScopes,
                Policy = request.Policy,
                WorkingDirectory = request.WorkingDirectory,
                RepositoryId = request.RepositoryId,
                BranchId = request.BranchId,
                BranchLanguageId = request.BranchLanguageId,
                LanguageCode = request.LanguageCode,
                AllowedTrackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { relative }
            };

            var first = new SourceInventoryBuilder().Build(request);
            var firstDigest = first.Files.Single().ContentDigest;

            File.WriteAllText(full, prefix + "TAIL-V2");
            var second = new SourceInventoryBuilder().Build(request);
            var secondDigest = second.Files.Single().ContentDigest;

            Assert.False(string.IsNullOrWhiteSpace(firstDigest));
            Assert.NotEqual(firstDigest, secondDigest);
            Assert.NotEqual(first.ContentHash, second.ContentHash);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Build_WithManifestByPath_ExcludesOpenedUnderSubmittedHaveOnly()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var tracked = "SampleProject/Source/SampleGameplay/Public/SampleGameplay.h";
            var opened = "SampleProject/Source/SampleGameplay/Private/SampleGameplay.cpp";
            var request = CreateRequest(root);
            request = new GenerationRequest
            {
                Snapshot = request.Snapshot,
                ResolvedScopes = request.ResolvedScopes,
                Policy = request.Policy,
                WorkingDirectory = request.WorkingDirectory,
                RepositoryId = request.RepositoryId,
                BranchId = request.BranchId,
                BranchLanguageId = request.BranchLanguageId,
                LanguageCode = request.LanguageCode,
                ManifestByPath = new Dictionary<string, WorkspaceManifestEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [tracked] = new WorkspaceManifestEntry
                    {
                        RelativePath = tracked,
                        HaveRevision = "10",
                        MatchesHaveContent = true,
                        IsOpened = false
                    },
                    [opened] = new WorkspaceManifestEntry
                    {
                        RelativePath = opened,
                        HaveRevision = "11",
                        MatchesHaveContent = false,
                        IsOpened = true,
                        OpenedAction = "edit"
                    }
                }
            };

            var inventory = new SourceInventoryBuilder().Build(request);

            Assert.Contains(inventory.Files, file => file.RelativePath == tracked);
            Assert.DoesNotContain(inventory.Files, file => file.RelativePath == opened);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Build_DeepNestedModule_IsNotDroppedByDepthLimit()
    {
        var root = CreateSampleWorkspace();
        try
        {
            var deepDir = Path.Combine(
                root,
                "SampleProject",
                "Source",
                "SampleGameplay",
                "Private",
                "A",
                "B",
                "C",
                "D",
                "E");
            Directory.CreateDirectory(deepDir);
            File.WriteAllText(Path.Combine(deepDir, "DeepFeature.cpp"), "// deep");

            var inventory = new SourceInventoryBuilder().Build(CreateRequest(root));
            Assert.Contains(
                inventory.Files,
                file => file.RelativePath.EndsWith("DeepFeature.cpp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    internal static string CreateSampleWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "odw-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Write(root, "SampleProject/SampleProject.uproject", "{ \"FileVersion\": 3 }");
        Write(root, "SampleProject/Source/SampleGameplay/SampleGameplay.Build.cs",
            """
            public class SampleGameplay : ModuleRules
            {
                public SampleGameplay(ReadOnlyTargetRules Target) : base(Target)
                {
                    PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine" });
                    PrivateDependencyModuleNames.AddRange(new string[] { "Slate" });
                }
            }
            """);
        Write(root, "SampleProject/Source/SampleGameplay/Public/SampleGameplay.h", "#pragma once\n");
        Write(root, "SampleProject/Source/SampleGameplay/Private/SampleGameplay.cpp", "// cpp\n");
        Write(root, "SampleProject/Source/SampleProjectEditor.Target.cs",
            """
            public class SampleProjectEditorTarget : TargetRules
            {
                public SampleProjectEditorTarget(TargetInfo Target) : base(Target) {}
            }
            """);

        Write(root, "SampleProject/Plugins/SamplePlugin/SamplePlugin.uplugin", "{ \"FileVersion\": 3 }");
        Write(root, "SampleProject/Plugins/SamplePlugin/Source/SamplePlugin/SamplePlugin.Build.cs",
            """
            public class SamplePlugin : ModuleRules
            {
                public SamplePlugin(ReadOnlyTargetRules Target) : base(Target)
                {
                    PublicDependencyModuleNames.AddRange(new string[] { "Core" });
                }
            }
            """);
        Write(root, "SampleProject/Plugins/SamplePlugin/Source/SamplePlugin/Public/SamplePlugin.h", "#pragma once\n");
        Write(root, "SampleProject/Plugins/SamplePlugin/Binaries/Win64/SamplePlugin.dll", "binary");
        Write(root, "SampleProject/Plugins/SamplePlugin/Intermediate/Build/Foo.cpp", "// intermediate");

        // Context-only engine content
        Write(root, "Engine/Source/Runtime/Core/Public/Core.h", "#pragma once\n");

        return root;
    }

    internal static GenerationRequest CreateRequest(string workingDirectory)
    {
        var document = new ScopeConfigurationDocument
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = WorkspaceContentPolicy.SubmittedHaveOnly,
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "game-source",
                    Root = "SampleProject/Source",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = [],
                    IncludedSuffixes =
                    [
                        ".h", ".hpp", ".inl", ".c", ".cc", ".cpp", ".as",
                        ".ini", ".json", ".yaml", ".yml", ".uproject", ".uplugin",
                        ".Build.cs", ".Target.cs", ".md"
                    ]
                },
                new DocumentScopeRuleDto
                {
                    Id = "game-plugins",
                    Root = "SampleProject/Plugins",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs =
                    [
                        "**/Binaries/**", "**/Intermediate/**", "**/Content/**", "**/Saved/**"
                    ]
                }
            ],
            ContextScope = new ContextScopeRuleDto
            {
                Roots = ["Engine/Source"],
                ReadOnly = true
            }
        };

        // uproject is under SampleProject root, not under Source/Plugins document scopes.
        // Add a project-root document scope so structural signals are visible in inventory tests.
        document.DocumentScopes.Add(new DocumentScopeRuleDto
        {
            Id = "game-project-root",
            Root = "SampleProject",
            IncludedPathGlobs = ["*.uproject", "Source/**", "Plugins/**"],
            ExcludedPathGlobs =
            [
                "**/Binaries/**", "**/Intermediate/**", "**/Content/**", "**/Saved/**"
            ],
            IncludedSuffixes =
            [
                ".h", ".hpp", ".inl", ".c", ".cc", ".cpp", ".as",
                ".ini", ".json", ".yaml", ".yml", ".uproject", ".uplugin",
                ".Build.cs", ".Target.cs", ".md"
            ]
        });

        var resolved = new ScopeConfigurationNormalizer().Normalize(document);
        return new GenerationRequest
        {
            Snapshot = new SnapshotIdentity
            {
                RepositoryId = "repo-sample",
                BranchId = "branch-main",
                ScopeConfigurationVersion = 1,
                TargetChangelist = "100",
                TrackedManifestHash = "manifest-hash",
                GenerationEngineVersion = GenerationEngineVersions.Hierarchical
            },
            ResolvedScopes = resolved,
            Policy = new GenerationPolicy
            {
                DomainFileBudget = 400,
                MaxPlanningDepth = 4,
                GenerateLeafContent = false,
                BlockOnCoverageErrors = false
            },
            WorkingDirectory = workingDirectory,
            RepositoryId = "repo-sample",
            BranchId = "branch-main",
            BranchLanguageId = "lang-zh",
            LanguageCode = "zh"
        };
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
