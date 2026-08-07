namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Converts raw Perforce file actions into logical old/new changes and pairs the
/// two records emitted for a move. Missing movedFile depot metadata is rejected
/// so callers fail closed instead of silently losing one side of a cross-scope
/// move. Partner workspace paths may be null when the partner is outside the
/// client view; the mapped side is still emitted for Scope evaluation.
/// </summary>
public static class PerforceChangeCollator
{
    public static IReadOnlyList<PerforceLogicalChange> Collate(
        IEnumerable<PerforceFileChange> fileChanges,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(fileChanges);
        ArgumentNullException.ThrowIfNull(pathComparer);

        var result = new List<PerforceLogicalChange>();
        var moveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in fileChanges)
        {
            if (IsMove(file.Action))
            {
                // Depot-level move pairing is mandatory. Workspace-relative partner
                // paths are optional when the partner falls outside the client map.
                if (string.IsNullOrWhiteSpace(file.MovedDepotPath))
                {
                    throw new InvalidDataException(
                        $"Perforce {file.Action} at CL {file.Changelist} is missing movedFile metadata: {file.DepotPath}");
                }

                var isMoveDelete = file.Action.Equals("move/delete", StringComparison.OrdinalIgnoreCase);
                var oldDepotPath = isMoveDelete ? file.DepotPath : file.MovedDepotPath;
                var newDepotPath = isMoveDelete ? file.MovedDepotPath : file.DepotPath;
                var oldPath = isMoveDelete ? file.WorkspaceRelativePath : file.MovedWorkspaceRelativePath;
                var newPath = isMoveDelete ? file.MovedWorkspaceRelativePath : file.WorkspaceRelativePath;
                // Deduplicate by depot endpoints so move/add + move/delete collapse once
                // even when one partner workspace path is unmapped.
                var key = $"{file.Changelist}\n{oldDepotPath}\n{newDepotPath}";
                if (!moveKeys.Add(key))
                {
                    continue;
                }

                result.Add(new PerforceLogicalChange(
                    file.Changelist,
                    "move",
                    oldDepotPath,
                    newDepotPath,
                    oldPath,
                    newPath,
                    file.FileType));
                continue;
            }

            if (IsDeletion(file.Action))
            {
                result.Add(new PerforceLogicalChange(
                    file.Changelist,
                    file.Action,
                    file.DepotPath,
                    null,
                    file.WorkspaceRelativePath,
                    null,
                    file.FileType));
            }
            else
            {
                result.Add(new PerforceLogicalChange(
                    file.Changelist,
                    file.Action,
                    null,
                    file.DepotPath,
                    null,
                    file.WorkspaceRelativePath,
                    file.FileType));
            }
        }

        return result;
    }

    private static bool IsMove(string action)
    {
        return action.Equals("move/add", StringComparison.OrdinalIgnoreCase)
               || action.Equals("move/delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeletion(string action)
    {
        return action.Equals("delete", StringComparison.OrdinalIgnoreCase)
               || action.Equals("purge", StringComparison.OrdinalIgnoreCase);
    }
}
