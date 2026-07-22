using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class BranchGenerationWorkerPerforceTests
{
    [Theory]
    [InlineData(RepositorySourceType.Perforce, "1234", "p4-initial", "1234")]
    [InlineData(RepositorySourceType.Perforce, null, "p4-initial", "p4-initial")]
    [InlineData(RepositorySourceType.Git, "1234", "abcdef", "abcdef")]
    public void ResolveCompletedTargetCommitId_PreservesOnlyNumericPerforceTarget(
        RepositorySourceType sourceType,
        string? requested,
        string processed,
        string expected)
    {
        Assert.Equal(
            expected,
            BranchGenerationWorker.ResolveCompletedTargetCommitId(sourceType, requested, processed));
    }
}
