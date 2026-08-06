using System.Text.Json;

namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgeFactIndexBuilder
{
    UeKnowledgeFactIndex Build(
        UeKnowledgeManifest manifest,
        IReadOnlyDictionary<string, string> shardContentsByRelativePath,
        IReadOnlyList<string>? extraWarnings = null);
}

public sealed class UeKnowledgeFactIndexBuilder : IUeKnowledgeFactIndexBuilder
{
    public UeKnowledgeFactIndex Build(
        UeKnowledgeManifest manifest,
        IReadOnlyDictionary<string, string> shardContentsByRelativePath,
        IReadOnlyList<string>? extraWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(shardContentsByRelativePath);

        var warnings = new List<string>();
        if (extraWarnings is { Count: > 0 })
        {
            warnings.AddRange(extraWarnings);
        }

        var classes = new List<UeClassFact>();
        var primaryAssets = new List<UePrimaryAssetFact>();
        var schemas = new List<UeDataAssetSchemaFact>();
        var tags = new List<UeGameplayTagFact>();
        var actions = new List<UeEditorActionFact>();
        var tools = new List<UeMcpToolContract>();

        foreach (var shard in manifest.Shards.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
        {
            var relative = UeKnowledgeDigest.NormalizePath(shard.RelativePath);
            if (!TryGetContent(shardContentsByRelativePath, relative, out var content))
            {
                warnings.Add($"构建索引时缺少分片: {relative}");
                continue;
            }

            try
            {
                switch (shard.Kind.ToLowerInvariant())
                {
                    case UeKnowledgeSchema.ShardKinds.Reflection:
                    {
                        var parsed = JsonSerializer.Deserialize<UeReflectionShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.Classes is { Count: > 0 })
                        {
                            classes.AddRange(parsed.Classes.Where(c => !string.IsNullOrWhiteSpace(c.StableId)));
                        }

                        break;
                    }
                    case UeKnowledgeSchema.ShardKinds.PrimaryAssets:
                    {
                        var parsed = JsonSerializer.Deserialize<UeAssetShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.PrimaryAssets is { Count: > 0 })
                        {
                            primaryAssets.AddRange(parsed.PrimaryAssets.Where(a => !string.IsNullOrWhiteSpace(a.StableId)));
                        }

                        break;
                    }
                    case UeKnowledgeSchema.ShardKinds.DataSchemas:
                    {
                        var parsed = JsonSerializer.Deserialize<UeDataSchemaShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.DataAssets is { Count: > 0 })
                        {
                            schemas.AddRange(parsed.DataAssets.Where(s => !string.IsNullOrWhiteSpace(s.StableId)));
                        }

                        break;
                    }
                    case UeKnowledgeSchema.ShardKinds.GameplayTags:
                    {
                        var parsed = JsonSerializer.Deserialize<UeGameplayTagShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.Tags is { Count: > 0 })
                        {
                            tags.AddRange(parsed.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Tag)));
                        }

                        break;
                    }
                    case UeKnowledgeSchema.ShardKinds.Workflows:
                    {
                        var parsed = JsonSerializer.Deserialize<UeWorkflowShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.EditorActions is { Count: > 0 })
                        {
                            actions.AddRange(parsed.EditorActions.Where(a => !string.IsNullOrWhiteSpace(a.StableId)));
                        }

                        break;
                    }
                    case UeKnowledgeSchema.ShardKinds.McpContracts:
                    {
                        var parsed = JsonSerializer.Deserialize<UeMcpContractShard>(content, UeKnowledgeDigest.JsonOptions);
                        if (parsed?.Tools is { Count: > 0 })
                        {
                            tools.AddRange(parsed.Tools.Where(t => !string.IsNullOrWhiteSpace(t.ToolName)));
                        }

                        break;
                    }
                    default:
                        // 未知 kind 已在 validator 中 warning
                        break;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"解析分片失败 {relative}: {ex.Message}");
            }
        }

        // 稳定排序
        classes = classes
            .GroupBy(c => c.StableId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.StableId, StringComparer.Ordinal)
            .ToList();
        primaryAssets = primaryAssets
            .GroupBy(a => a.StableId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => a.StableId, StringComparer.Ordinal)
            .ToList();
        schemas = schemas
            .GroupBy(s => s.StableId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.StableId, StringComparer.Ordinal)
            .ToList();
        tags = tags
            .GroupBy(t => t.Tag, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Tag, StringComparer.Ordinal)
            .ToList();
        actions = actions
            .GroupBy(a => a.StableId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => a.StableId, StringComparer.Ordinal)
            .ToList();
        tools = tools
            .GroupBy(t => t.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isPartial = manifest.Completeness != UeKnowledgeCompletenessDto.Complete
                        || manifest.Errors.Count > 0
                        || manifest.TruncationNotes.Count > 0;

        var index = new UeKnowledgeFactIndex
        {
            SchemaVersion = manifest.SchemaVersion,
            PackageDigest = string.Empty,
            SemanticDigest = string.Empty,
            ProjectId = manifest.Project.ProjectId.Trim(),
            BuildChangelist = manifest.BuildChangelist.Trim(),
            EngineVersion = manifest.EngineVersion,
            Completeness = manifest.Completeness.ToString(),
            IsPartial = isPartial,
            Classes = classes,
            PrimaryAssets = primaryAssets,
            DataAssetSchemas = schemas,
            GameplayTags = tags,
            EditorActions = actions,
            McpTools = tools,
            Warnings = warnings,
            PrivacyFilterNotes = manifest.PrivacyFilterNotes
        };

        return index;
    }

    private static bool TryGetContent(
        IReadOnlyDictionary<string, string> map,
        string relative,
        out string content)
    {
        if (map.TryGetValue(relative, out content!))
        {
            return true;
        }

        var match = map.FirstOrDefault(pair =>
            string.Equals(
                UeKnowledgeDigest.NormalizePath(pair.Key),
                relative,
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match.Key))
        {
            content = match.Value;
            return true;
        }

        content = string.Empty;
        return false;
    }
}
