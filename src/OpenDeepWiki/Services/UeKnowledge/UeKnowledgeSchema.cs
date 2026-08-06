namespace OpenDeepWiki.Services.UeKnowledge;

/// <summary>
/// Schema 版本常量与 exporter/schema 兼容矩阵。
/// </summary>
public static class UeKnowledgeSchema
{
    public const string CurrentSchemaVersion = "1.0";

    public const int CurrentSchemaMajor = 1;

    public const int CurrentSchemaMinor = 0;

    /// <summary>
    /// 受支持的 schema major 集合。破坏性变更提升 major。
    /// </summary>
    public static readonly IReadOnlySet<int> SupportedSchemaMajors =
        new HashSet<int> { 1 };

    public static readonly IReadOnlyList<string> RequiredShardKinds =
    [
        ShardKinds.Reflection,
        ShardKinds.GameplayTags
    ];

    public static readonly IReadOnlyList<string> KnownShardKinds =
    [
        ShardKinds.Reflection,
        ShardKinds.PrimaryAssets,
        ShardKinds.DataSchemas,
        ShardKinds.GameplayTags,
        ShardKinds.Workflows,
        ShardKinds.McpContracts
    ];

    public static class ShardKinds
    {
        public const string Reflection = "reflection";
        public const string PrimaryAssets = "primary-assets";
        public const string DataSchemas = "data-schemas";
        public const string GameplayTags = "gameplay-tags";
        public const string Workflows = "workflows";
        public const string McpContracts = "mcp-contracts";
    }

    public static class ExportSources
    {
        public const string Editor = "Editor";
        public const string Commandlet = "Commandlet";
        public const string Cook = "Cook";
        public const string PackagedBuild = "PackagedBuild";
    }

    /// <summary>
    /// exporter major 与 schema major 的兼容关系。
    /// key = schema major，value = 兼容的 exporter major 前缀（如 "1."）。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> ExporterCompatibility =
        new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = ["1."]
        };

    public static bool TryParseSchemaVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out major))
        {
            return false;
        }

        if (parts.Length >= 2 && !int.TryParse(parts[1], out minor))
        {
            return false;
        }

        return true;
    }

    public static bool IsSchemaSupported(string? schemaVersion)
    {
        if (!TryParseSchemaVersion(schemaVersion, out var major, out _))
        {
            return false;
        }

        return SupportedSchemaMajors.Contains(major);
    }

    public static bool IsExporterCompatible(string? schemaVersion, string? exporterVersion)
    {
        if (!TryParseSchemaVersion(schemaVersion, out var major, out _))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(exporterVersion))
        {
            return false;
        }

        if (!ExporterCompatibility.TryGetValue(major, out var prefixes))
        {
            return false;
        }

        var normalized = exporterVersion.Trim();
        return prefixes.Any(prefix =>
            normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }
}
