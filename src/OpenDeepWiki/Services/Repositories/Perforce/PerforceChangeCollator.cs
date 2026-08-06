namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Converts raw Perforce file actions into logical old/new changes and pairs the
/// two records emitted for a move. Missing move metadata is rejected so callers
/// fail closed instead of silently losing one side of a cross-scope move.
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
        var moveKeys = new HashSet<string>(pathComparer);

        foreach (var file in fileChanges)
        {
            if (IsMove(file.Action))
            {
                if (string.IsNullOrWhiteSpace(file.MovedDepotPath)
                    || string.IsNullOrWhiteSpace(file.MovedWorkspaceRelativePath))
                {
                    throw new InvalidDataException(
                        $"Perforce {file.Action} at CL {file.Changelist} is missing movedFile metadata: {file.DepotPath}");
                }

                var isMoveDelete = file.Action.Equals("move/delete", StringComparison.OrdinalIgnoreCase);
                var oldDepotPath = isMoveDelete ? file.DepotPath : file.MovedDepotPath;
                var newDepotPath = isMoveDelete ? file.MovedDepotPath : file.DepotPath;
                var oldPath = isMoveDelete ? file.WorkspaceRelativePath : file.MovedWorkspaceRelativePath;
                var newPath = isMoveDelete ? file.MovedWorkspaceRelativePath : file.WorkspaceRelativePath;
                var key = $"{file.Changelist}\n{oldPath}\n{newPath}";
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
