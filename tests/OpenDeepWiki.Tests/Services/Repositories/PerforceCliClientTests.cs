using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.Services.Repositories.Perforce;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class PerforceCliClientTests
{
    [Fact]
    public void ParseChangelists_ParsesTaggedRecords()
    {
        const string output = """
            ... change 102
            ... user buildbot
            ... desc second change
            ... change 101
            ... user alice
            ... desc first change
            """;

        var changes = PerforceCliClient.ParseChangelists(output);

        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal(102, change.Number);
                Assert.Equal("buildbot", change.User);
                Assert.Equal("second change", change.Description);
            },
            change => Assert.Equal(101, change.Number));
    }

    [Fact]
    public void ParseChangelists_KeepsMultiLineDescriptionForSkipWikiFilter()
    {
        const string output = """
            ... change 105
            ... user alice
            ... desc first line of the summary
            please [skip-wiki] this change
            more notes
            ... change 104
            ... user bob
            ... desc single line
            """;

        var changes = PerforceCliClient.ParseChangelists(output);

        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal(105, change.Number);
                Assert.Contains("[skip-wiki]", change.Description, StringComparison.Ordinal);
                Assert.Contains("first line of the summary", change.Description, StringComparison.Ordinal);
            },
            change =>
            {
                Assert.Equal(104, change.Number);
                Assert.Equal("single line", change.Description);
            });
    }

    [Fact]
    public void ParseDescribedFiles_SupportsIndexedTaggedFields()
    {
        const string output = """
            ... change 102
            ... depotFile0 //depot/Game/Source/Foo.cpp
            ... action0 edit
            ... type0 text+k
            ... depotFile1 //depot/Game/Content/Hero.uasset
            ... action1 add
            ... type1 binary+l
            """;

        var files = PerforceCliClient.ParseDescribedFiles(output);

        Assert.Collection(
            files,
            file =>
            {
                Assert.Equal("//depot/Game/Source/Foo.cpp", file.DepotPath);
                Assert.Equal("edit", file.Action);
                Assert.Equal("text+k", file.FileType);
            },
            file => Assert.Equal("binary+l", file.FileType));
    }

    [Fact]
    public async Task GetFileChangesAsync_MapsDepotPathsAndRejectsPathsOutsideWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"odw-p4-{Guid.NewGuid():N}"));
        try
        {
            var runner = new Mock<IPerforceCommandRunner>(MockBehavior.Strict);
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "describe"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 200
                    ... depotFile0 //depot/Game/Source/Foo.cpp
                    ... action0 edit
                    ... type0 text
                    ... depotFile1 //depot/Other/Bar.cpp
                    ... action1 edit
                    ... type1 text
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "where"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, $"""
                    ... depotFile //depot/Game/Source/Foo.cpp
                    ... clientFile //client/Game/Source/Foo.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "Foo.cpp")}
                    ... depotFile //depot/Other/Bar.cpp
                    ... clientFile //client/Other/Bar.cpp
                    ... path {Path.Combine(workspace.Parent!.FullName, "outside", "Bar.cpp")}
                    """, string.Empty));

            var client = CreateClient(runner.Object);
            var files = await client.GetFileChangesAsync(workspace.FullName, 200);

            var file = Assert.Single(files);
            Assert.Equal("Source/Foo.cpp", file.WorkspaceRelativePath);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetChangelistsAsync_UsesLeftOpenRightClosedRangeAndSorts()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"odw-p4-{Guid.NewGuid():N}"));
        try
        {
            var runner = new Mock<IPerforceCommandRunner>(MockBehavior.Strict);
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args.Contains("...@101,@103")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 103
                    ... user bob
                    ... desc third
                    ... change 101
                    ... user alice
                    ... desc first
                    """, string.Empty));

            var changes = await CreateClient(runner.Object).GetChangelistsAsync(workspace.FullName, 100, 103, 501);

            Assert.Equal([101L, 103L], changes.Select(change => change.Number).ToArray());
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static PerforceCliClient CreateClient(IPerforceCommandRunner runner)
    {
        var options = new Mock<IOptionsMonitor<PerforceOptions>>();
        options.SetupGet(value => value.CurrentValue).Returns(new PerforceOptions());
        return new PerforceCliClient(
            runner,
            options.Object,
            Mock.Of<ILogger<PerforceCliClient>>());
    }
}
