using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Scope;

public class RepositoryFileSelectionPolicyTests
{
    private readonly IRepositoryFileSelectionPolicy _policy;

    public RepositoryFileSelectionPolicyTests()
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
                    // no includedSuffixes → default set
                }
            ],
            ContextScope = new ContextScopeRuleDto
            {
                Roots = ["Engine/Source", "Shared/Plugins", "Shared/Source"],
                ReadOnly = true,
                PreferTrackedFiles = true,
                MaxFileBytes = 2 * 1024 * 1024
            },
            ChangeTriggerScope = new ChangeTriggerScopeRuleDto
            {
                InheritsDocumentScopes = true,
                AdditionalRoots = []
            }
        };

        var resolved = new ScopeConfigurationNormalizer().Normalize(document);
        _policy = new RepositoryFileSelectionPolicyFactory().Create(resolved);
    }

    [Theory]
    [InlineData("SampleProject/Source/Game/Foo.cpp", true, FileSelectionReasonCodes.AcceptedDocument)]
    [InlineData("SampleProject/Source/Game/Module.Build.cs", true, FileSelectionReasonCodes.AcceptedDocument)]
    [InlineData("SampleProject/Plugins/Foo/Source/Bar.h", true, FileSelectionReasonCodes.AcceptedDocument)]
    [InlineData("SampleProject/Plugins/Foo/Binaries/Win64/x.dll", false, FileSelectionReasonCodes.PathExcluded)]
    [InlineData("Engine/Source/Runtime/Core/Public/Core.h", false, FileSelectionReasonCodes.ContextNotDocument)]
    [InlineData("Content/Hero.uasset", false, FileSelectionReasonCodes.OutsideAnyScope)]
    public void IsDocumentCandidate_UsesScopeRules(string path, bool expected, string reasonCode)
    {
        var decision = _policy.EvaluateDocumentCandidate(path);
        Assert.Equal(expected, decision.Accepted);
        Assert.Equal(reasonCode, decision.ReasonCode);
    }

    [Theory]
    [InlineData("C:/abs/path.cpp")]
    [InlineData("\\\\server\\share\\a.cpp")]
    [InlineData("../escape/a.cpp")]
    [InlineData("SampleProject/Source/secrets.json")]
    public void SafetyRejects_DangerousPaths(string path)
    {
        var decision = _policy.EvaluateDocumentCandidate(path);
        Assert.False(decision.Accepted);
        Assert.StartsWith("safety.", decision.ReasonCode);
    }

    [Fact]
    public void CompositeSuffix_LongestMatch_PrefersBuildCs()
    {
        var match = ScopePathUtility.FindLongestMatchingSuffix(
            "SampleProject/Source/Foo.Build.cs",
            [".cs", ".Build.cs", ".Target.cs"]);

        Assert.Equal(".Build.cs", match);
        Assert.True(_policy.IsDocumentCandidate("SampleProject/Source/Foo.Build.cs"));
    }

    [Fact]
    public void ContextScope_NeverBecomesDocumentViaFiletype()
    {
        var metadata = new SourceFileMetadata
        {
            FileType = "text",
            IsTracked = true,
            MatchesHaveContent = true
        };

        var decision = _policy.EvaluateDocumentCandidate("Engine/Source/Runtime/Core/Public/Core.h", metadata);
        Assert.False(decision.Accepted);
        Assert.Equal(FileSelectionReasonCodes.ContextNotDocument, decision.ReasonCode);
        Assert.True(_policy.CanReadAsContext("Engine/Source/Runtime/Core/Public/Core.h", metadata));
    }

    [Fact]
    public void SubmittedHaveOnly_RejectsOpenedAndMismatch()
    {
        var opened = _policy.EvaluateDocumentCandidate(
            "SampleProject/Source/Game/Foo.cpp",
            new SourceFileMetadata { IsTracked = true, IsOpened = true, OpenedAction = "edit" });
        Assert.False(opened.Accepted);
        Assert.Equal(FileSelectionReasonCodes.WorkspaceOpened, opened.ReasonCode);

        var mismatch = _policy.EvaluateDocumentCandidate(
            "SampleProject/Source/Game/Foo.cpp",
            new SourceFileMetadata { IsTracked = true, MatchesHaveContent = false });
        Assert.False(mismatch.Accepted);
        Assert.Equal(FileSelectionReasonCodes.WorkspaceContentMismatch, mismatch.ReasonCode);
    }

    [Fact]
    public void ShouldTriggerUpdate_InheritsDocumentScopesByDefault()
    {
        Assert.True(_policy.ShouldTriggerUpdate("SampleProject/Source/Game/Foo.cpp"));
        Assert.False(_policy.ShouldTriggerUpdate("Engine/Source/Runtime/Core/Public/Core.h"));
    }

    [Fact]
    public void Move_DistinguishesDocumentContextExcluded()
    {
        var move = _policy.EvaluateMove(
            "SampleProject/Source/Game/Foo.cpp",
            "Engine/Source/Runtime/Core/Public/Foo.h");

        Assert.Equal(MoveScopeTransition.DocumentToContext, move.Transition);
        Assert.True(move.OldPath.Accepted);
        Assert.True(move.NewPath.Accepted);
        Assert.Equal(FileSelectionReasonCodes.AcceptedContext, move.NewPath.ReasonCode);
    }

    [Fact]
    public void ShouldPruneDirectory_OutsideScope()
    {
        Assert.True(_policy.ShouldPruneDirectory("Content", FileSelectionOperation.DocumentCandidate));
        Assert.False(_policy.ShouldPruneDirectory("SampleProject/Source", FileSelectionOperation.DocumentCandidate));
        Assert.False(_policy.ShouldPruneDirectory("Engine", FileSelectionOperation.ContextRead));
    }

    [Fact]
    public void MostSpecificRoot_Wins()
    {
        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "broad",
                    Root = "SampleProject",
                    IncludedSuffixes = [".cpp"]
                },
                new DocumentScopeRuleDto
                {
                    Id = "narrow",
                    Root = "SampleProject/Source",
                    IncludedSuffixes = [".h"]
                }
            ]
        };

        var policy = new RepositoryFileSelectionPolicyFactory()
            .Create(new ScopeConfigurationNormalizer().Normalize(document));

        var header = policy.EvaluateDocumentCandidate("SampleProject/Source/Foo.h");
        Assert.True(header.Accepted);
        Assert.Equal("narrow", header.MatchedScopeId);

        var cppUnderSource = policy.EvaluateDocumentCandidate("SampleProject/Source/Foo.cpp");
        Assert.False(cppUnderSource.Accepted);
        Assert.Equal(FileSelectionReasonCodes.SuffixNotIncluded, cppUnderSource.ReasonCode);
    }

    [Fact]
    public void DefaultSuffixes_AppliedWhenDocumentScopeOmitsIncludedSuffixes()
    {
        Assert.True(_policy.IsDocumentCandidate("SampleProject/Plugins/Foo/Source/Bar.md"));
        Assert.False(_policy.IsDocumentCandidate("SampleProject/Plugins/Foo/Source/Bar.txt"));
    }

    [Fact]
    public void SamePath_SameReasonAcrossOperationsWhenDocument()
    {
        const string path = "SampleProject/Source/Game/Foo.cpp";
        var document = _policy.EvaluateDocumentCandidate(path);
        var trigger = _policy.EvaluateChangeTrigger(path);
        Assert.True(document.Accepted);
        Assert.True(trigger.Accepted);
        Assert.Equal(document.MatchedScopeId, trigger.MatchedScopeId);
    }

    [Theory]
    [InlineData("Foo.cpp", "**/*.cpp", true)]
    [InlineData("Binaries/Win64/x.dll", "**/Binaries/**", true)]
    [InlineData("Nested/Binaries/Win64/x.dll", "**/Binaries/**", true)]
    [InlineData("Source/Foo.cpp", "**/Binaries/**", false)]
    public void Glob_MatchesRootLevelPatterns(string path, string glob, bool expected)
    {
        Assert.Equal(expected, ScopePathUtility.IsGlobMatch(path, glob));
    }

    [Fact]
    public void EmptyRoot_DocumentScope_CoversRepositoryRoot()
    {
        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "repo-root",
                    Root = string.Empty,
                    IncludedSuffixes = [".cpp"]
                }
            ]
        };

        var validation = new ScopeConfigurationValidator().Validate(document);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        var policy = new RepositoryFileSelectionPolicyFactory()
            .Create(new ScopeConfigurationNormalizer().Normalize(document));
        Assert.True(policy.IsDocumentCandidate("Foo.cpp"));
    }
}
