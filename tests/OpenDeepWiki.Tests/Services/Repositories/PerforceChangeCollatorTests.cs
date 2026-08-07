using OpenDeepWiki.Services.Repositories.Perforce;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class PerforceChangeCollatorTests
{
    [Fact]
    public void Collate_PairsMoveRecordsAndRetainsOldAndNewPaths()
    {
        var changes = PerforceChangeCollator.Collate(
        [
            new PerforceFileChange(
                101,
                "//depot/Game/Source/Old.cpp",
                "Source/Old.cpp",
                "move/delete",
                "text",
                "//depot/Game/Shared/New.cpp",
                "Shared/New.cpp"),
            new PerforceFileChange(
                101,
                "//depot/Game/Shared/New.cpp",
                "Shared/New.cpp",
                "move/add",
                "text",
                "//depot/Game/Source/Old.cpp",
                "Source/Old.cpp")
        ], StringComparer.OrdinalIgnoreCase);

        var move = Assert.Single(changes);
        Assert.Equal("move", move.Action);
        Assert.Equal("Source/Old.cpp", move.OldWorkspaceRelativePath);
        Assert.Equal("Shared/New.cpp", move.NewWorkspaceRelativePath);
    }

    [Fact]
    public void Collate_MoveWithoutMovedFileMetadataFailsClosed()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            PerforceChangeCollator.Collate(
            [
                new PerforceFileChange(
                    101,
                    "//depot/Game/Shared/New.cpp",
                    "Shared/New.cpp",
                    "move/add",
                    "text")
            ], StringComparer.OrdinalIgnoreCase));

        Assert.Contains("movedFile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collate_MoveWithUnmappedPartnerWorkspacePathEmitsOneSidedLogicalMove()
    {
        var changes = PerforceChangeCollator.Collate(
        [
            new PerforceFileChange(
                101,
                "//depot/Game/Source/Old.cpp",
                "Source/Old.cpp",
                "move/delete",
                "text",
                "//depot/Game/Outside/New.cpp",
                null)
        ], StringComparer.OrdinalIgnoreCase);

        var move = Assert.Single(changes);
        Assert.Equal("move", move.Action);
        Assert.Equal("Source/Old.cpp", move.OldWorkspaceRelativePath);
        Assert.Null(move.NewWorkspaceRelativePath);
        Assert.Equal("//depot/Game/Outside/New.cpp", move.NewDepotPath);
    }
}
