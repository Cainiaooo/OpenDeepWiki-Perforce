using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.Services.Repositories.Perforce;
using OpenDeepWiki.Services.Repositories.Scope;
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
                    It.Is<IReadOnlyList<string>>(args => args.Any(arg =>
                        arg.EndsWith($"{Path.DirectorySeparatorChar}...@101,@103", StringComparison.Ordinal))),
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

    [Fact]
    public async Task GetChangelistsAsync_UsesAllExplicitFilespecsAndDeduplicatesChanges()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"odw-p4-{Guid.NewGuid():N}"));
        try
        {
            var sourceFilespec = Path.Combine(workspace.FullName, "SampleProject", "Source", "...");
            var pluginFilespec = Path.Combine(workspace.FullName, "SampleProject", "Plugins", "...");
            var runner = new Mock<IPerforceCommandRunner>(MockBehavior.Strict);
            // Each filespec is queried separately so the -m budget is not shared.
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args =>
                        args[0] == "changes"
                        && args.Contains(sourceFilespec + "@101,@103")
                        && !args.Contains(pluginFilespec + "@101,@103")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 103
                    ... user alice
                    ... desc source result
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args =>
                        args[0] == "changes"
                        && args.Contains(pluginFilespec + "@101,@103")
                        && !args.Contains(sourceFilespec + "@101,@103")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 103
                    ... user alice
                    ... desc duplicated plugin result
                    """, string.Empty));

            var changes = await CreateClient(runner.Object).GetChangelistsAsync(
                workspace.FullName,
                100,
                103,
                501,
                [sourceFilespec, pluginFilespec]);

            var change = Assert.Single(changes);
            Assert.Equal(103, change.Number);
            runner.Verify(command => command.RunTaggedAsync(
                workspace.FullName,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetChangelistsAsync_PerFilespecBudgetPreservesDistinctChangelists()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"odw-p4-{Guid.NewGuid():N}"));
        try
        {
            var sourceFilespec = Path.Combine(workspace.FullName, "Source", "...");
            var pluginFilespec = Path.Combine(workspace.FullName, "Plugins", "...");
            var runner = new Mock<IPerforceCommandRunner>(MockBehavior.Strict);
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args =>
                        args.Contains("-m")
                        && args.Contains("2")
                        && args.Contains(sourceFilespec + "@101,@110")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 101
                    ... user alice
                    ... desc source only
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args =>
                        args.Contains("-m")
                        && args.Contains("2")
                        && args.Contains(pluginFilespec + "@101,@110")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... change 102
                    ... user bob
                    ... desc plugin only
                    """, string.Empty));

            var changes = await CreateClient(runner.Object).GetChangelistsAsync(
                workspace.FullName,
                100,
                110,
                2,
                [sourceFilespec, pluginFilespec]);

            Assert.Equal([101L, 102L], changes.Select(change => change.Number).ToArray());
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetFileChangesAsync_UnmappedMovedPartnerKeepsDepotMetadataAndContinues()
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
                    ... depotFile0 //depot/Game/Source/Old.cpp
                    ... action0 move/delete
                    ... type0 text
                    ... depotFile1 //depot/Game/Source/Edit.cpp
                    ... action1 edit
                    ... type1 text
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "fstat"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... depotFile //depot/Game/Source/Old.cpp
                    ... movedFile //depot/Game/Outside/New.cpp
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "where"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, $"""
                    ... depotFile //depot/Game/Source/Old.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "Old.cpp")}
                    ... depotFile //depot/Game/Source/Edit.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "Edit.cpp")}
                    """, string.Empty));

            var files = await CreateClient(runner.Object).GetFileChangesAsync(workspace.FullName, 200);

            Assert.Equal(2, files.Count);
            var move = Assert.Single(files, file => file.Action == "move/delete");
            Assert.Equal("//depot/Game/Outside/New.cpp", move.MovedDepotPath);
            Assert.Null(move.MovedWorkspaceRelativePath);
            Assert.Contains(files, file => file.WorkspaceRelativePath == "Source/Edit.cpp" && file.Action == "edit");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetFileChangesAsync_EscapesDepotPathMetacharactersInFstat()
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
                    ... depotFile0 //depot/Game/Source/Foo@Bar.cpp
                    ... action0 move/delete
                    ... type0 text
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args =>
                        args[0] == "fstat"
                        && args.Any(arg => arg.Contains("%40", StringComparison.Ordinal)
                                           && arg.EndsWith("@200", StringComparison.Ordinal))),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... depotFile //depot/Game/Source/Foo@Bar.cpp
                    ... movedFile //depot/Game/Source/New.cpp
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "where"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, $"""
                    ... depotFile //depot/Game/Source/Foo@Bar.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "Foo@Bar.cpp")}
                    ... depotFile //depot/Game/Source/New.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "New.cpp")}
                    """, string.Empty));

            var files = await CreateClient(runner.Object).GetFileChangesAsync(workspace.FullName, 200);

            var file = Assert.Single(files);
            Assert.Equal("Source/New.cpp", file.MovedWorkspaceRelativePath);
            runner.Verify(command => command.RunTaggedAsync(
                workspace.FullName,
                It.Is<IReadOnlyList<string>>(args =>
                    args[0] == "fstat"
                    && args.Contains("//depot/Game/Source/Foo%40Bar.cpp@200")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetFileChangesAsync_RetainsMappedMoveEndpoints()
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
                    ... depotFile0 //depot/Game/Source/Old.cpp
                    ... action0 move/delete
                    ... type0 text
                    ... depotFile1 //depot/Game/Shared/New.cpp
                    ... action1 move/add
                    ... type1 text
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "fstat"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, """
                    ... depotFile //depot/Game/Source/Old.cpp
                    ... movedFile //depot/Game/Shared/New.cpp
                    ... depotFile //depot/Game/Shared/New.cpp
                    ... movedFile //depot/Game/Source/Old.cpp
                    """, string.Empty));
            runner.Setup(command => command.RunTaggedAsync(
                    workspace.FullName,
                    It.Is<IReadOnlyList<string>>(args => args[0] == "where"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PerforceCommandResult(0, $"""
                    ... depotFile //depot/Game/Source/Old.cpp
                    ... path {Path.Combine(workspace.FullName, "Source", "Old.cpp")}
                    ... depotFile //depot/Game/Shared/New.cpp
                    ... path {Path.Combine(workspace.FullName, "Shared", "New.cpp")}
                    """, string.Empty));

            var files = await CreateClient(runner.Object).GetFileChangesAsync(workspace.FullName, 200);

            Assert.Equal(2, files.Count);
            Assert.All(files, file => Assert.NotNull(file.MovedWorkspaceRelativePath));
            Assert.Contains(files, file =>
                file.WorkspaceRelativePath == "Source/Old.cpp"
                && file.MovedWorkspaceRelativePath == "Shared/New.cpp");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildChangeTriggerFilespecs_UsesDocumentAndAdditionalRoots()
    {
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExampleWorkspace"));
        var configuration = new ResolvedScopeConfiguration
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = WorkspaceContentPolicy.SubmittedHaveOnly,
            DocumentScopes =
            [
                new ResolvedDocumentScope
                {
                    Id = "source",
                    Root = "SampleProject/Source",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = [],
                    IncludedSuffixes = [".cpp"],
                    AcceptAllTextFiles = false
                }
            ],
            ContextScope = null,
            ChangeTriggerScope = new ResolvedChangeTriggerScope
            {
                InheritsDocumentScopes = true,
                AdditionalRoots = ["Shared/Config"],
                IncludedPathGlobs = ["**"],
                ExcludedPathGlobs = []
            },
            ContentHash = "scope-hash",
            NormalizedJson = "{}"
        };

        var filespecs = PerforceFilespecBuilder.BuildChangeTriggerFilespecs(workspace, configuration);

        Assert.Equal(2, filespecs.Count);
        Assert.All(filespecs, filespec => Assert.True(Path.IsPathRooted(filespec)));
        Assert.Contains(filespecs, filespec => filespec.EndsWith(
            Path.Combine("SampleProject", "Source", "..."),
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filespecs, filespec => filespec.EndsWith(
            Path.Combine("Shared", "Config", "..."),
            StringComparison.OrdinalIgnoreCase));
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
