using OpenDeepWiki.Services.Repositories.Perforce;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class PerforceFilterPipelineTests
{
    private readonly IChangelistFilterPipeline _pipeline = new ChangelistFilterPipeline(
        [new ChangelistUserFilter(), new ChangelistDescriptionFilter()],
        [new FileActionFilter(), new FilePathFilter(), new FileContentTypeFilter()]);

    [Fact]
    public void EvaluateChangelist_MatchesSkipWikiOnContinuationLine()
    {
        var changelist = new PerforceChangelist(
            200,
            "alice",
            "summary line\nplease [skip-wiki] this CL\nfooter");

        var decision = _pipeline.EvaluateChangelist(changelist, new PerforceFilterOptions());

        Assert.False(decision.Included);
        Assert.Contains("description matched", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Source/Game/Foo.cpp", "text", true)]
    [InlineData("Config/DefaultGame.ini", "text", true)]
    [InlineData("Source/Game/README.custom", "text+k", true)]
    [InlineData("Content/Hero.uasset", "binary+l", false)]
    [InlineData("Source/Game/Hero.uasset", "binary", false)]
    [InlineData("Intermediate/Generated/Foo.cpp", "text", false)]
    public void EvaluateFile_UsesUePathAndContentComposition(string path, string fileType, bool expected)
    {
        var file = new PerforceFileChange(
            101,
            $"//depot/Game/{path}",
            path,
            "edit",
            fileType);

        var result = _pipeline.EvaluateFile(file, new PerforceFilterOptions());

        Assert.Equal(expected, result.Included);
    }

    [Fact]
    public void EvaluateFile_HonorsDepotPathGlobAndActionFilter()
    {
        var options = new PerforceFilterOptions
        {
            IncludedPathGlobs = ["//depot/Shared/**"],
            IncludedActions = ["edit"]
        };

        var added = new PerforceFileChange(
            102,
            "//depot/Shared/Foo.cpp",
            "External/Foo.cpp",
            "add",
            "text");

        var result = _pipeline.EvaluateFile(added, options);

        Assert.False(result.Included);
        Assert.Contains("action", result.Reason);
    }

    [Theory]
    [InlineData("normal change", "alice", true)]
    [InlineData("fix [skip-wiki] generated content", "alice", false)]
    [InlineData("normal change", "buildbot", false)]
    public void EvaluateChangelist_FiltersDescriptionAndAuthor(string description, string user, bool expected)
    {
        var options = new PerforceFilterOptions
        {
            ExcludedUsers = ["buildbot"]
        };

        var result = _pipeline.EvaluateChangelist(new PerforceChangelist(103, user, description), options);

        Assert.Equal(expected, result.Included);
    }

    [Fact]
    public void FilterOverride_ReplacesOnlyExplicitValues()
    {
        var options = new PerforceFilterOptions();
        options.Apply(new PerforceFilterOverrideOptions
        {
            MaxFiles = 12,
            IncludedPathGlobs = ["Engine/**"]
        });

        Assert.Equal(12, options.MaxFiles);
        Assert.Equal(["Engine/**"], options.IncludedPathGlobs);
        Assert.Contains(".cpp", options.IncludedSuffixes);
        Assert.True(options.AllowTextFileType);
    }
}
