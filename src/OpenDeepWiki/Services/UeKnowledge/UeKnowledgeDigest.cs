using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDeepWiki.Services.UeKnowledge;

public static class UeKnowledgeDigest
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256Hex(string content)
        => ComputeSha256Hex(Encoding.UTF8.GetBytes(content));

    public static string ComputeFileDigest(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 包逻辑摘要：schema + project + build CL + 排序后分片 (path, digest)。
    /// 不含导出时间，保证同一逻辑内容 digest 稳定。
    /// </summary>
    public static string ComputePackageDigest(UeKnowledgeManifest manifest)
    {
        var payload = new
        {
            schemaVersion = Normalize(manifest.SchemaVersion),
            exporterVersion = Normalize(manifest.ExporterVersion),
            projectId = Normalize(manifest.Project.ProjectId),
            branch = Normalize(manifest.Project.Branch),
            buildChangelist = Normalize(manifest.BuildChangelist),
            engineVersion = Normalize(manifest.EngineVersion),
            targetPlatform = Normalize(manifest.TargetPlatform),
            exportSource = Normalize(manifest.ExportSource),
            completeness = manifest.Completeness.ToString(),
            shards = manifest.Shards
                .OrderBy(s => s.RelativePath, StringComparer.Ordinal)
                .Select(s => new
                {
                    path = NormalizePath(s.RelativePath),
                    digest = Normalize(s.Digest),
                    kind = Normalize(s.Kind),
                    objectCount = s.ObjectCount
                })
                .ToArray()
        };

        return ComputeSha256Hex(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// 语义摘要：仅消费类/标签/工具等事实身份，忽略导出时间与分片文件名变化。
    /// </summary>
    public static string ComputeSemanticDigest(UeKnowledgeFactIndex index)
    {
        var payload = new
        {
            projectId = Normalize(index.ProjectId),
            buildChangelist = Normalize(index.BuildChangelist),
            classes = index.Classes
                .OrderBy(c => c.StableId, StringComparer.Ordinal)
                .Select(c => new
                {
                    id = Normalize(c.StableId),
                    name = Normalize(c.Name),
                    super = Normalize(c.SuperClass),
                    source = Normalize(c.Source),
                    module = Normalize(c.ModuleName),
                    properties = c.Properties
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => $"{Normalize(p.Name)}:{Normalize(p.Type)}")
                        .ToArray(),
                    functions = c.Functions
                        .OrderBy(f => f.Name, StringComparer.Ordinal)
                        .Select(f => Normalize(f.Name))
                        .ToArray()
                })
                .ToArray(),
            primaryAssets = index.PrimaryAssets
                .OrderBy(a => a.StableId, StringComparer.Ordinal)
                .Select(a => new
                {
                    id = Normalize(a.StableId),
                    type = Normalize(a.AssetType),
                    name = Normalize(a.Name)
                })
                .ToArray(),
            schemas = index.DataAssetSchemas
                .OrderBy(s => s.StableId, StringComparer.Ordinal)
                .Select(s => new
                {
                    id = Normalize(s.StableId),
                    type = Normalize(s.TypeName),
                    fields = s.Fields
                        .OrderBy(f => f.Name, StringComparer.Ordinal)
                        .Select(f => $"{Normalize(f.Name)}:{Normalize(f.Type)}:{f.IsArray}")
                        .ToArray()
                })
                .ToArray(),
            tags = index.GameplayTags
                .OrderBy(t => t.Tag, StringComparer.Ordinal)
                .Select(t => Normalize(t.Tag))
                .ToArray(),
            tools = index.McpTools
                .OrderBy(t => t.ToolName, StringComparer.Ordinal)
                .Select(t => new
                {
                    name = Normalize(t.ToolName),
                    schema = Normalize(t.InputSchemaJson),
                    minCl = Normalize(t.MinBuildChangelist),
                    maxCl = Normalize(t.MaxBuildChangelist)
                })
                .ToArray(),
            actions = index.EditorActions
                .OrderBy(a => a.StableId, StringComparer.Ordinal)
                .Select(a => Normalize(a.StableId))
                .ToArray()
        };

        return ComputeSha256Hex(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
