namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Perforce changelist metadata returned by <c>p4 changes</c>.
/// </summary>
public sealed record PerforceChangelist(
    long Number,
    string User,
    string Description);

/// <summary>
/// A single file change from a submitted Perforce changelist.
/// </summary>
public sealed record PerforceFileChange(
    long Changelist,
    string DepotPath,
    string WorkspaceRelativePath,
    string Action,
    string FileType,
    string? MovedDepotPath = null,
    string? MovedWorkspaceRelativePath = null);

/// <summary>
/// A source-level change after move/add and move/delete records have been paired.
/// Old/new paths are both retained so cross-scope moves can be assessed without
/// depending on the destination file still existing.
/// </summary>
public sealed record PerforceLogicalChange(
    long Changelist,
    string Action,
    string? OldDepotPath,
    string? NewDepotPath,
    string? OldWorkspaceRelativePath,
    string? NewWorkspaceRelativePath,
    string FileType);

/// <summary>
/// Result of a tagged Perforce CLI command.
/// </summary>
public sealed record PerforceCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

/// <summary>
/// Failure reported while invoking or parsing the Perforce CLI.
/// </summary>
public sealed class PerforceCommandException : Exception
{
    public PerforceCommandException(string message, bool isTransient = false, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
