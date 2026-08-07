using OpenDeepWiki.Entities;
using Xunit;

namespace OpenDeepWiki.Tests.Entities;

public class RepositorySourceTests
{
    [Fact]
    public void EncodePerforcePath_RoundTripsThroughParse()
    {
        const string workspaceRoot = "/data/p4/SampleProject";

        var stored = RepositorySource.EncodePerforcePath(workspaceRoot);
        var parsed = RepositorySource.Parse(stored);

        Assert.StartsWith("p4::", stored);
        Assert.Equal(RepositorySourceType.Perforce, parsed.SourceType);
        Assert.Equal(workspaceRoot, parsed.Location);
        Assert.True(RepositorySource.IsPerforce(stored));
        Assert.False(RepositorySource.IsGit(stored));
    }

    [Fact]
    public void Parse_DistinguishesPerforceFromLocalDirectoryAndGit()
    {
        var perforce = RepositorySource.EncodePerforcePath("C:/p4/root");
        var local = RepositorySource.EncodeLocalDirectoryPath("C:/p4/root");

        Assert.NotEqual(perforce, local);
        Assert.Equal(RepositorySourceType.Perforce, RepositorySource.Parse(perforce).SourceType);
        Assert.Equal(RepositorySourceType.LocalDirectory, RepositorySource.Parse(local).SourceType);
        Assert.Equal(RepositorySourceType.Git, RepositorySource.Parse("https://github.com/demo/repo.git").SourceType);
    }
}
