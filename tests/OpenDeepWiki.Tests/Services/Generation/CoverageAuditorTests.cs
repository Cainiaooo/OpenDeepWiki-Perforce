using OpenDeepWiki.Services.Generation;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Generation;

public class CoverageAuditorTests
{
    [Fact]
    public void Audit_MarksUnassignedAndDuplicateFiles()
    {
        var inventory = new SourceInventory
        {
            SnapshotIdentity = "snap",
            ContentHash = "hash",
            WorkingDirectory = "C:/tmp",
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new InventoryFileEntry
                {
                    RelativePath = "SampleProject/Source/A/A.cpp",
                    ScopeId = "game-source",
                    FileKind = "Source",
                    IsEntryPoint = false
                },
                new InventoryFileEntry
                {
                    RelativePath = "SampleProject/Source/B/B.cpp",
                    ScopeId = "game-source",
                    FileKind = "Source",
                    IsEntryPoint = false
                },
                new InventoryFileEntry
                {
                    RelativePath = "SampleProject/Source/Shared/Shared.h",
                    ScopeId = "game-source",
                    FileKind = "Header",
                    IsEntryPoint = false
                }
            ],
            Modules =
            [
                new InventoryModuleEntry
                {
                    ModuleId = "module-a",
                    Name = "A",
                    RootPath = "SampleProject/Source/A",
                    ScopeId = "game-source",
                    BuildCsPath = "SampleProject/Source/A/A.Build.cs"
                },
                new InventoryModuleEntry
                {
                    ModuleId = "module-orphan",
                    Name = "Orphan",
                    RootPath = "SampleProject/Source/Orphan",
                    ScopeId = "game-source"
                }
            ],
            Plugins = [],
            Projects = [],
            Targets = []
        };

        var domains = new[]
        {
            new PlannedDomain
            {
                DomainId = "domain-a",
                ScopeId = "game-source",
                DisplayName = "A",
                IncludedRoots = ["SampleProject/Source/A"],
                ModuleIds = ["module-a"],
                FileCount = 1
            },
            new PlannedDomain
            {
                DomainId = "domain-b",
                ScopeId = "game-source",
                DisplayName = "B",
                IncludedRoots = ["SampleProject/Source/B", "SampleProject/Source/Shared"],
                FileCount = 2
            },
            new PlannedDomain
            {
                DomainId = "domain-dup",
                ScopeId = "game-source",
                DisplayName = "DupShared",
                IncludedRoots = ["SampleProject/Source/Shared"],
                FileCount = 1
            }
        };

        var manifests = new[]
        {
            new ScopeManifest
            {
                PageId = "page-a",
                ScopeId = "game-source",
                DomainId = "domain-a",
                TopicSummary = "A overview",
                IncludedRoots = ["SampleProject/Source/A"],
                EntryFiles = ["SampleProject/Source/A/A.Build.cs"],
                RequiredEvidenceKinds = [EvidenceKind.Document],
                ModuleId = "module-a"
            },
            new ScopeManifest
            {
                PageId = "page-b",
                ScopeId = "game-source",
                DomainId = "domain-b",
                TopicSummary = "B overview",
                IncludedRoots = ["SampleProject/Source/B", "SampleProject/Source/Shared"],
                EntryFiles = ["SampleProject/Source/B/B.cpp"],
                RequiredEvidenceKinds = [EvidenceKind.Document]
            },
            new ScopeManifest
            {
                PageId = "page-dup",
                ScopeId = "game-source",
                DomainId = "domain-dup",
                TopicSummary = "Shared again",
                IncludedRoots = ["SampleProject/Source/Shared"],
                EntryFiles = ["SampleProject/Source/Shared/Shared.h"],
                RequiredEvidenceKinds = [EvidenceKind.Document]
            }
        };

        var report = new CoverageAuditor().Audit("snap", inventory, domains, manifests);

        Assert.Contains(report.Items, item =>
            item.RelativePath == "SampleProject/Source/Shared/Shared.h"
            && item.Status == CoverageItemStatus.DuplicateAssignment);

        Assert.Contains(report.Items, item =>
            item.SubjectKind == "Module"
            && item.SubjectId == "module-orphan"
            && item.Status == CoverageItemStatus.Unassigned);

        Assert.True(report.HasBlockingErrors);
        Assert.Contains(report.BlockingReasons, reason => reason.Contains("Unassigned modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_FlagsContextOnlyPages()
    {
        var inventory = new SourceInventory
        {
            SnapshotIdentity = "snap",
            ContentHash = "hash",
            WorkingDirectory = "C:/tmp",
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Files = [],
            Modules = [],
            Plugins = [],
            Projects = [],
            Targets = []
        };

        var manifests = new[]
        {
            new ScopeManifest
            {
                PageId = "page-empty",
                ScopeId = "game-source",
                DomainId = "domain-x",
                TopicSummary = "no entries",
                IncludedRoots = ["SampleProject/Source/X"],
                EntryFiles = [],
                RequiredEvidenceKinds = [EvidenceKind.Context]
            }
        };

        var report = new CoverageAuditor().Audit("snap", inventory, [], manifests);
        Assert.Contains(report.Items, item => item.Status == CoverageItemStatus.ContextOnlyEvidence);
        Assert.True(report.HasBlockingErrors);
    }
}
