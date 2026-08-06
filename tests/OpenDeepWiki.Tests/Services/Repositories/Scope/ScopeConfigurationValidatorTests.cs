using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Scope;

public class ScopeConfigurationValidatorTests
{
    private readonly ScopeConfigurationValidator _validator = new();
    private readonly ScopeConfigurationNormalizer _normalizer = new();

    [Fact]
    public void Validate_AcceptsPhase3Example()
    {
        var document = CreateExampleDocument();
        var result = _validator.Validate(document);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsAndUnsafeRoots()
    {
        var document = CreateExampleDocument();
        document.DocumentScopes.Add(new DocumentScopeRuleDto
        {
            Id = "game-source",
            Root = "../escape",
            IncludedPathGlobs = ["**"]
        });

        var result = _validator.Validate(document);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("safety", StringComparison.OrdinalIgnoreCase)
                                                 || error.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsIdenticalRoots()
    {
        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto { Id = "a", Root = "SampleProject/Source" },
                new DocumentScopeRuleDto { Id = "b", Root = "SampleProject/Source" }
            ]
        };

        var result = _validator.Validate(document);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Overlapping", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_AppliesDefaultChangeTriggerAndSuffixes()
    {
        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "game-plugins",
                    Root = "SampleProject/Plugins",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = ["**/Binaries/**"]
                }
            ]
            // no changeTriggerScope
        };

        var resolved = _normalizer.Normalize(document);
        Assert.True(resolved.ChangeTriggerScope.InheritsDocumentScopes);
        Assert.Contains(".cpp", resolved.DocumentScopes[0].IncludedSuffixes);
        Assert.Contains(".Build.cs", resolved.DocumentScopes[0].IncludedSuffixes);
        Assert.False(string.IsNullOrWhiteSpace(resolved.ContentHash));
        Assert.Contains("\"schemaVersion\":1", resolved.NormalizedJson);
    }

    [Fact]
    public void Normalize_IsStableForSameLogicalConfig()
    {
        var left = _normalizer.Normalize(CreateExampleDocument());
        var right = _normalizer.Normalize(CreateExampleDocument());
        Assert.Equal(left.ContentHash, right.ContentHash);
        Assert.Equal(left.NormalizedJson, right.NormalizedJson);
    }

    [Fact]
    public void Validate_AllowsEmptyStringRootAsRepositoryRoot()
    {
        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto { Id = "root", Root = string.Empty }
            ]
        };

        var result = _validator.Validate(document);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsNullContextRootsWithoutThrowing()
    {
        var document = CreateExampleDocument();
        document.ContextScope = new ContextScopeRuleDto
        {
            Roots = null!,
            MaxFileBytes = 1024
        };

        var result = _validator.Validate(document);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("contextScope.roots", StringComparison.Ordinal));
    }

    private static ScopeConfigurationDocument CreateExampleDocument() => new()
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
            Roots = ["Engine/Source", "Shared/Plugins", "Shared/Source"],
            ReadOnly = true,
            PreferTrackedFiles = true,
            MaxFileBytes = 2097152
        },
        ChangeTriggerScope = new ChangeTriggerScopeRuleDto
        {
            InheritsDocumentScopes = true,
            AdditionalRoots = []
        }
    };
}
