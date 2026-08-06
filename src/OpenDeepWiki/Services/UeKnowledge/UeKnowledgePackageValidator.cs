using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgePackageValidator
{
    UeKnowledgeValidationResult Validate(
        UeKnowledgePackageLoadResult loadResult,
        string? expectedProjectId = null,
        string? expectedBuildChangelist = null,
        bool strictBuildChangelistMatch = true);
}

public sealed class UeKnowledgePackageValidator : IUeKnowledgePackageValidator
{
    private static readonly Regex AbsolutePathRegex = new(
        @"^(?:[A-Za-z]:[\\/]|\\\\|/)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IUeKnowledgeFactIndexBuilder _factIndexBuilder;

    public UeKnowledgePackageValidator(IUeKnowledgeFactIndexBuilder factIndexBuilder)
    {
        _factIndexBuilder = factIndexBuilder;
    }

    public UeKnowledgeValidationResult Validate(
        UeKnowledgePackageLoadResult loadResult,
        string? expectedProjectId = null,
        string? expectedBuildChangelist = null,
        bool strictBuildChangelistMatch = true)
    {
        ArgumentNullException.ThrowIfNull(loadResult);

        var errors = new List<string>(loadResult.Errors);
        var warnings = new List<string>(loadResult.Warnings);
        var manifest = loadResult.Manifest;

        if (!UeKnowledgeSchema.IsSchemaSupported(manifest.SchemaVersion))
        {
            errors.Add($"不支持的 schema version: {manifest.SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.ExporterVersion))
        {
            errors.Add("exporterVersion 不能为空");
        }
        else if (!UeKnowledgeSchema.IsExporterCompatible(manifest.SchemaVersion, manifest.ExporterVersion))
        {
            errors.Add(
                $"exporterVersion {manifest.ExporterVersion} 与 schema {manifest.SchemaVersion} 不在兼容矩阵中");
        }

        if (string.IsNullOrWhiteSpace(manifest.Project.ProjectId))
        {
            errors.Add("project.projectId 不能为空");
        }
        else if (!string.IsNullOrWhiteSpace(expectedProjectId)
                 && !string.Equals(manifest.Project.ProjectId, expectedProjectId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"project identity 不匹配: package={manifest.Project.ProjectId}, expected={expectedProjectId}");
        }

        if (string.IsNullOrWhiteSpace(manifest.BuildChangelist))
        {
            errors.Add("buildChangelist 不能为空");
        }
        else if (!string.IsNullOrWhiteSpace(expectedBuildChangelist)
                 && !string.Equals(manifest.BuildChangelist, expectedBuildChangelist, StringComparison.Ordinal))
        {
            var message =
                $"buildChangelist 不匹配: package={manifest.BuildChangelist}, expected={expectedBuildChangelist}";
            if (strictBuildChangelistMatch)
            {
                errors.Add(message);
            }
            else
            {
                warnings.Add(message);
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.ExportSource))
        {
            errors.Add("exportSource 不能为空");
        }

        if (manifest.Shards.Count == 0)
        {
            errors.Add("至少需要一个 shard");
        }

        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shard in manifest.Shards)
        {
            var relative = UeKnowledgeDigest.NormalizePath(shard.RelativePath);
            if (string.IsNullOrWhiteSpace(shard.Name))
            {
                errors.Add("存在未命名 shard");
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                errors.Add($"shard {shard.Name} 缺少 relativePath");
                continue;
            }

            if (!paths.Add(relative))
            {
                errors.Add($"重复 shard 路径: {relative}");
            }

            if (relative.Contains("..", StringComparison.Ordinal)
                || AbsolutePathRegex.IsMatch(relative)
                || Path.IsPathRooted(relative))
            {
                errors.Add($"shard 路径非法: {relative}");
            }

            if (string.IsNullOrWhiteSpace(shard.Digest) || shard.Digest.Length != 64)
            {
                errors.Add($"shard {relative} digest 无效（需要 64 位小写 hex SHA-256）");
            }

            if (string.IsNullOrWhiteSpace(shard.Kind))
            {
                errors.Add($"shard {relative} 缺少 kind");
            }
            else
            {
                kinds.Add(shard.Kind);
                if (!UeKnowledgeSchema.KnownShardKinds.Contains(shard.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add($"未知 shard kind（将忽略内容解析）: {shard.Kind}");
                }
            }

            if (!loadResult.ShardContentsByRelativePath.TryGetValue(relative, out var content))
            {
                // 大小写不敏感再找一次
                var match = loadResult.ShardContentsByRelativePath
                    .FirstOrDefault(pair =>
                        string.Equals(
                            UeKnowledgeDigest.NormalizePath(pair.Key),
                            relative,
                            StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(match.Key))
                {
                    errors.Add($"缺少 shard 内容: {relative}");
                    continue;
                }

                content = match.Value;
            }

            var actualDigest = UeKnowledgeDigest.ComputeSha256Hex(content);
            if (!string.Equals(actualDigest, shard.Digest, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"shard digest 不符: {relative} expected={shard.Digest} actual={actualDigest}");
            }

            // 已知 kind：按结构反序列化；未知 kind：至少是合法 JSON。
            if (!TryValidateShardContent(shard.Kind, relative, content, errors, warnings))
            {
                // 错误已写入 errors
            }
        }

        foreach (var required in UeKnowledgeSchema.RequiredShardKinds)
        {
            if (!kinds.Contains(required))
            {
                errors.Add($"缺少必需 shard kind: {required}");
            }
        }

        if (manifest.Completeness == UeKnowledgeCompletenessDto.Partial
            || manifest.Completeness == UeKnowledgeCompletenessDto.Truncated)
        {
            if (manifest.Errors.Count == 0 && manifest.TruncationNotes.Count == 0)
            {
                warnings.Add("manifest 标记为 Partial/Truncated，但未提供 errors/truncationNotes");
            }
        }
        else if (manifest.Errors.Count > 0)
        {
            // 完整包不应夹带错误；强制视为 partial 语义
            warnings.Add("manifest.completeness=Complete 但包含 errors；将按 Partial 处理");
        }

        if (errors.Count > 0)
        {
            return new UeKnowledgeValidationResult
            {
                IsValid = false,
                Errors = errors,
                Warnings = warnings
            };
        }

        var factIndex = _factIndexBuilder.Build(manifest, loadResult.ShardContentsByRelativePath, warnings);
        var packageDigest = UeKnowledgeDigest.ComputePackageDigest(manifest);
        var semanticDigest = UeKnowledgeDigest.ComputeSemanticDigest(factIndex);

        // 用稳定 semantic digest 回写
        factIndex = new UeKnowledgeFactIndex
        {
            SchemaVersion = factIndex.SchemaVersion,
            PackageDigest = packageDigest,
            SemanticDigest = semanticDigest,
            ProjectId = factIndex.ProjectId,
            BuildChangelist = factIndex.BuildChangelist,
            EngineVersion = factIndex.EngineVersion,
            Completeness = factIndex.Completeness,
            IsPartial = factIndex.IsPartial,
            Classes = factIndex.Classes,
            PrimaryAssets = factIndex.PrimaryAssets,
            DataAssetSchemas = factIndex.DataAssetSchemas,
            GameplayTags = factIndex.GameplayTags,
            EditorActions = factIndex.EditorActions,
            McpTools = factIndex.McpTools,
            Warnings = factIndex.Warnings,
            PrivacyFilterNotes = factIndex.PrivacyFilterNotes
        };

        var package = new UeKnowledgePackageDocument
        {
            Manifest = manifest,
            PackageRootPath = loadResult.PackageRootPath,
            PackageDigest = packageDigest,
            SemanticDigest = semanticDigest,
            ShardContentsByRelativePath = loadResult.ShardContentsByRelativePath,
            Warnings = warnings
        };

        return new UeKnowledgeValidationResult
        {
            IsValid = true,
            Errors = errors,
            Warnings = warnings,
            Package = package,
            FactIndex = factIndex
        };
    }

    /// <summary>
    /// 校验分片内容。已知 kind 必须能按结构反序列化且包含有效对象集合；
    /// 未知 kind 仅要求合法 JSON（并已在外层 warning）。
    /// </summary>
    private static bool TryValidateShardContent(
        string? kind,
        string relativePath,
        string content,
        List<string> errors,
        List<string> warnings)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            errors.Add($"shard JSON 无效: {relativePath}: {ex.Message}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(kind)
            || !UeKnowledgeSchema.KnownShardKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            switch (kind.ToLowerInvariant())
            {
                case UeKnowledgeSchema.ShardKinds.Reflection:
                {
                    var parsed = JsonSerializer.Deserialize<UeReflectionShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (reflection)");
                        return false;
                    }

                    if (parsed.Classes.Count == 0)
                    {
                        errors.Add($"shard 缺少有效 classes: {relativePath}");
                        return false;
                    }

                    if (parsed.Classes.Any(c => string.IsNullOrWhiteSpace(c.StableId) || string.IsNullOrWhiteSpace(c.Name)))
                    {
                        errors.Add($"shard classes 存在空 StableId/Name: {relativePath}");
                        return false;
                    }

                    return true;
                }
                case UeKnowledgeSchema.ShardKinds.GameplayTags:
                {
                    var parsed = JsonSerializer.Deserialize<UeGameplayTagShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (gameplay-tags)");
                        return false;
                    }

                    if (parsed.Tags.Count == 0)
                    {
                        errors.Add($"shard 缺少有效 tags: {relativePath}");
                        return false;
                    }

                    if (parsed.Tags.Any(t => string.IsNullOrWhiteSpace(t.Tag)))
                    {
                        errors.Add($"shard tags 存在空 Tag: {relativePath}");
                        return false;
                    }

                    return true;
                }
                case UeKnowledgeSchema.ShardKinds.PrimaryAssets:
                {
                    var parsed = JsonSerializer.Deserialize<UeAssetShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (primary-assets)");
                        return false;
                    }

                    if (parsed.PrimaryAssets.Any(a => string.IsNullOrWhiteSpace(a.StableId)))
                    {
                        errors.Add($"shard primaryAssets 存在空 StableId: {relativePath}");
                        return false;
                    }

                    return true;
                }
                case UeKnowledgeSchema.ShardKinds.DataSchemas:
                {
                    var parsed = JsonSerializer.Deserialize<UeDataSchemaShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (data-schemas)");
                        return false;
                    }

                    if (parsed.DataAssets.Any(a => string.IsNullOrWhiteSpace(a.StableId)))
                    {
                        errors.Add($"shard dataAssets 存在空 StableId: {relativePath}");
                        return false;
                    }

                    return true;
                }
                case UeKnowledgeSchema.ShardKinds.Workflows:
                {
                    var parsed = JsonSerializer.Deserialize<UeWorkflowShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (workflows)");
                        return false;
                    }

                    if (parsed.EditorActions.Any(a => string.IsNullOrWhiteSpace(a.StableId)))
                    {
                        errors.Add($"shard editorActions 存在空 StableId: {relativePath}");
                        return false;
                    }

                    return true;
                }
                case UeKnowledgeSchema.ShardKinds.McpContracts:
                {
                    var parsed = JsonSerializer.Deserialize<UeMcpContractShard>(content, UeKnowledgeDigest.JsonOptions);
                    if (parsed is null)
                    {
                        errors.Add($"shard 结构无效: {relativePath} (mcp-contracts)");
                        return false;
                    }

                    if (parsed.Tools.Any(t => string.IsNullOrWhiteSpace(t.ToolName)))
                    {
                        errors.Add($"shard tools 存在空 ToolName: {relativePath}");
                        return false;
                    }

                    return true;
                }
                default:
                    warnings.Add($"未处理的已知 kind 校验分支: {kind}");
                    return true;
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"shard 结构反序列化失败: {relativePath}: {ex.Message}");
            return false;
        }
    }
}
