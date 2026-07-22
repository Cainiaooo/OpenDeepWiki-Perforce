namespace OpenDeepWiki.Services.Repositories.Perforce;

/// <summary>
/// Perforce CLI and changelist filtering settings. Configuration is re-read for every event.
/// </summary>
public sealed class PerforceOptions
{
    public const string SectionName = "Perforce";

    public string Command { get; set; } = "p4";

    public int CommandTimeoutSeconds { get; set; } = 30;

    public int MaxRetryAttempts { get; set; } = 2;

    public int RetryBaseDelayMs { get; set; } = 250;

    public int WhereBatchSize { get; set; } = 64;

    public string? Port { get; set; }

    public string? User { get; set; }

    public string? Client { get; set; }

    public string? TicketsFile { get; set; }

    public string? ConfigFile { get; set; }

    public PerforceFilterOptions Filter { get; set; } = new();

    /// <summary>
    /// Per-repository overrides keyed by repository ID or by "organization/repository".
    /// Repository ID takes precedence.
    /// </summary>
    public Dictionary<string, PerforceFilterOverrideOptions> RepositoryFilters { get; set; } = new();
}

/// <summary>
/// UE-friendly defaults. Path, action and content classification are combined with AND semantics;
/// content classification accepts an allowed suffix OR a text-like p4 filetype.
/// </summary>
public sealed class PerforceFilterOptions
{
    public List<string> IncludedPathGlobs { get; set; } =
    [
        "Source/**",
        "Script/**",
        "Config/**",
        "Plugins/**",
        "*.uproject",
        "*.uplugin",
        "*.Build.cs",
        "*.Target.cs"
    ];

    public List<string> ExcludedPathGlobs { get; set; } =
    [
        "Content/**",
        "Binaries/**",
        "Intermediate/**",
        "Saved/**",
        "DerivedDataCache/**"
    ];

    public List<string> IncludedSuffixes { get; set; } =
    [
        ".cpp", ".c", ".cc", ".h", ".hpp", ".inl", ".cs", ".as",
        ".ini", ".json", ".xml", ".yaml", ".yml", ".uproject", ".uplugin",
        ".Build.cs", ".Target.cs"
    ];

    public List<string> ExcludedSuffixes { get; set; } =
    [
        ".uasset", ".umap", ".png", ".jpg", ".jpeg", ".tga", ".dds",
        ".fbx", ".wav", ".mp3", ".ogg", ".mp4", ".avi"
    ];

    public List<string> IncludedActions { get; set; } =
    [
        "add", "edit", "delete", "branch", "integrate", "move/add", "move/delete"
    ];

    public List<string> ExcludedUsers { get; set; } = [];

    public List<string> ExcludedDescriptionPatterns { get; set; } =
    [
        "\\[skip-wiki\\]",
        "#nodoc\\b"
    ];

    public bool AllowTextFileType { get; set; } = true;

    public bool CaseSensitivePaths { get; set; }

    public int MaxChangelists { get; set; } = 500;

    public int MaxFiles { get; set; } = 1000;

    public PerforceFilterOptions Clone() => new()
    {
        IncludedPathGlobs = [.. IncludedPathGlobs],
        ExcludedPathGlobs = [.. ExcludedPathGlobs],
        IncludedSuffixes = [.. IncludedSuffixes],
        ExcludedSuffixes = [.. ExcludedSuffixes],
        IncludedActions = [.. IncludedActions],
        ExcludedUsers = [.. ExcludedUsers],
        ExcludedDescriptionPatterns = [.. ExcludedDescriptionPatterns],
        AllowTextFileType = AllowTextFileType,
        CaseSensitivePaths = CaseSensitivePaths,
        MaxChangelists = MaxChangelists,
        MaxFiles = MaxFiles
    };

    public void Apply(PerforceFilterOverrideOptions value)
    {
        IncludedPathGlobs = value.IncludedPathGlobs ?? IncludedPathGlobs;
        ExcludedPathGlobs = value.ExcludedPathGlobs ?? ExcludedPathGlobs;
        IncludedSuffixes = value.IncludedSuffixes ?? IncludedSuffixes;
        ExcludedSuffixes = value.ExcludedSuffixes ?? ExcludedSuffixes;
        IncludedActions = value.IncludedActions ?? IncludedActions;
        ExcludedUsers = value.ExcludedUsers ?? ExcludedUsers;
        ExcludedDescriptionPatterns = value.ExcludedDescriptionPatterns ?? ExcludedDescriptionPatterns;
        AllowTextFileType = value.AllowTextFileType ?? AllowTextFileType;
        CaseSensitivePaths = value.CaseSensitivePaths ?? CaseSensitivePaths;
        MaxChangelists = value.MaxChangelists ?? MaxChangelists;
        MaxFiles = value.MaxFiles ?? MaxFiles;
    }
}

/// <summary>
/// Nullable repository override; omitted values inherit the global filter configuration.
/// </summary>
public sealed class PerforceFilterOverrideOptions
{
    public List<string>? IncludedPathGlobs { get; set; }
    public List<string>? ExcludedPathGlobs { get; set; }
    public List<string>? IncludedSuffixes { get; set; }
    public List<string>? ExcludedSuffixes { get; set; }
    public List<string>? IncludedActions { get; set; }
    public List<string>? ExcludedUsers { get; set; }
    public List<string>? ExcludedDescriptionPatterns { get; set; }
    public bool? AllowTextFileType { get; set; }
    public bool? CaseSensitivePaths { get; set; }
    public int? MaxChangelists { get; set; }
    public int? MaxFiles { get; set; }
}
