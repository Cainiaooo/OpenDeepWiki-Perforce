namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Minimal Perforce access needed by changelist-driven incremental updates.
/// </summary>
public interface IPerforceClient
{
    Task<long?> GetLatestChangelistAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PerforceChangelist>> GetChangelistsAsync(
        string workspaceRoot,
        long afterChangelist,
        long throughChangelist,
        int maxResults,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PerforceFileChange>> GetFileChangesAsync(
        string workspaceRoot,
        long changelist,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs p4 commands with tagged output. Kept separate so the client parser has contract tests.
/// </summary>
public interface IPerforceCommandRunner
{
    Task<PerforceCommandResult> RunTaggedAsync(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
