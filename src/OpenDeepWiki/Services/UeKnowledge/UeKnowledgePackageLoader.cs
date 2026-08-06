using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgePackageLoader
{
    /// <summary>
    /// 从 package root 加载 manifest 与分片原文（不做完整语义校验）。
    /// </summary>
    UeKnowledgePackageLoadResult LoadFromDirectory(string packageRootPath);

    /// <summary>
    /// 从已解析 manifest + 分片内容字典构建加载结果。
    /// </summary>
    UeKnowledgePackageLoadResult LoadFromContents(
        UeKnowledgeManifest manifest,
        IReadOnlyDictionary<string, string> shardContentsByRelativePath,
        string packageRootPath = "");
}

public sealed class UeKnowledgePackageLoadResult
{
    public required UeKnowledgeManifest Manifest { get; init; }

    public required string PackageRootPath { get; init; }

    public required IReadOnlyDictionary<string, string> ShardContentsByRelativePath { get; init; }

    public List<string> Errors { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}

public sealed class UeKnowledgePackageLoader : IUeKnowledgePackageLoader
{
    private static readonly Regex AbsolutePathHint = new(
        @"([A-Za-z]:\\|\\\\|/Users/|/home/|/var/|/tmp/)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public UeKnowledgePackageLoadResult LoadFromDirectory(string packageRootPath)
    {
        if (string.IsNullOrWhiteSpace(packageRootPath))
        {
            return Fail("packageRootPath 不能为空");
        }

        var root = Path.GetFullPath(packageRootPath);
        if (!Directory.Exists(root))
        {
            return Fail($"包目录不存在: {root}");
        }

        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Fail("缺少 manifest.json");
        }

        UeKnowledgeManifest? manifest;
        try
        {
            var manifestText = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<UeKnowledgeManifest>(manifestText, UeKnowledgeDigest.JsonOptions);
        }
        catch (Exception ex)
        {
            return Fail($"manifest.json 解析失败: {ex.Message}");
        }

        if (manifest is null)
        {
            return Fail("manifest.json 为空或无效");
        }

        var shards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var errors = new List<string>();

        foreach (var shard in manifest.Shards.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
        {
            var relative = UeKnowledgeDigest.NormalizePath(shard.RelativePath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                errors.Add($"分片 {shard.Name} 缺少 relativePath");
                continue;
            }

            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                errors.Add($"分片路径不安全: {relative}");
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"分片路径逃逸包根: {relative}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                errors.Add($"分片文件不存在: {relative}");
                continue;
            }

            var content = File.ReadAllText(fullPath);
            shards[relative] = content;

            if (AbsolutePathHint.IsMatch(content))
            {
                warnings.Add($"分片可能包含本机绝对路径痕迹: {relative}");
            }
        }

        return new UeKnowledgePackageLoadResult
        {
            Manifest = manifest,
            PackageRootPath = root,
            ShardContentsByRelativePath = shards,
            Errors = errors,
            Warnings = warnings
        };
    }

    public UeKnowledgePackageLoadResult LoadFromContents(
        UeKnowledgeManifest manifest,
        IReadOnlyDictionary<string, string> shardContentsByRelativePath,
        string packageRootPath = "")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(shardContentsByRelativePath);

        var normalized = shardContentsByRelativePath.ToDictionary(
            pair => UeKnowledgeDigest.NormalizePath(pair.Key),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        foreach (var pair in normalized)
        {
            if (AbsolutePathHint.IsMatch(pair.Value))
            {
                warnings.Add($"分片可能包含本机绝对路径痕迹: {pair.Key}");
            }
        }

        return new UeKnowledgePackageLoadResult
        {
            Manifest = manifest,
            PackageRootPath = packageRootPath,
            ShardContentsByRelativePath = normalized,
            Warnings = warnings
        };
    }

    private static UeKnowledgePackageLoadResult Fail(string error)
        => new()
        {
            Manifest = new UeKnowledgeManifest(),
            PackageRootPath = string.Empty,
            ShardContentsByRelativePath = new Dictionary<string, string>(),
            Errors = [error]
        };
}
