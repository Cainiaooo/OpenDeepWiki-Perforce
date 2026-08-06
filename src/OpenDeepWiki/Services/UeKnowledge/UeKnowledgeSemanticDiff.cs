namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgeSemanticDiff
{
    UeKnowledgeSemanticDiffResult Diff(UeKnowledgeFactIndex from, UeKnowledgeFactIndex to);
}

public sealed class UeKnowledgeSemanticDiff : IUeKnowledgeSemanticDiff
{
    public UeKnowledgeSemanticDiffResult Diff(UeKnowledgeFactIndex from, UeKnowledgeFactIndex to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var result = new UeKnowledgeSemanticDiffResult
        {
            FromSemanticDigest = from.SemanticDigest,
            ToSemanticDigest = to.SemanticDigest
        };

        if (string.Equals(from.SemanticDigest, to.SemanticDigest, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(from.SemanticDigest))
        {
            return result;
        }

        DiffClasses(from, to, result);
        DiffTags(from, to, result);
        DiffTools(from, to, result);
        DiffAssets(from, to, result);
        DiffSchemas(from, to, result);

        // 领域提示：模块名 / 类名前缀，供 WP5 影响分析消费
        var domainHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var classId in result.AddedClassIds.Concat(result.RemovedClassIds).Concat(result.ChangedClassIds))
        {
            var module = from.Classes.Concat(to.Classes)
                .FirstOrDefault(c => string.Equals(c.StableId, classId, StringComparison.Ordinal))
                ?.ModuleName;
            if (!string.IsNullOrWhiteSpace(module))
            {
                domainHints.Add(module);
            }
        }

        if (result.AddedTags.Count > 0 || result.RemovedTags.Count > 0)
        {
            domainHints.Add("GameplayTags");
        }

        if (result.AddedToolNames.Count > 0
            || result.RemovedToolNames.Count > 0
            || result.ChangedToolNames.Count > 0)
        {
            domainHints.Add("UeMcpContracts");
        }

        result.AffectedDomainHints = domainHints.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    private static void DiffClasses(
        UeKnowledgeFactIndex from,
        UeKnowledgeFactIndex to,
        UeKnowledgeSemanticDiffResult result)
    {
        var fromMap = from.Classes.ToDictionary(c => c.StableId, StringComparer.Ordinal);
        var toMap = to.Classes.ToDictionary(c => c.StableId, StringComparer.Ordinal);

        foreach (var id in toMap.Keys.Except(fromMap.Keys, StringComparer.Ordinal).OrderBy(x => x))
        {
            result.AddedClassIds.Add(id);
        }

        foreach (var id in fromMap.Keys.Except(toMap.Keys, StringComparer.Ordinal).OrderBy(x => x))
        {
            result.RemovedClassIds.Add(id);
        }

        foreach (var id in fromMap.Keys.Intersect(toMap.Keys, StringComparer.Ordinal).OrderBy(x => x))
        {
            var left = SerializeClass(fromMap[id]);
            var right = SerializeClass(toMap[id]);
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                result.ChangedClassIds.Add(id);
            }
        }

        if (result.AddedClassIds.Count > 0
            || result.RemovedClassIds.Count > 0
            || result.ChangedClassIds.Count > 0)
        {
            result.ChangedKinds.Add(UeKnowledgeSchema.ShardKinds.Reflection);
        }
    }

    private static void DiffTags(
        UeKnowledgeFactIndex from,
        UeKnowledgeFactIndex to,
        UeKnowledgeSemanticDiffResult result)
    {
        var fromTags = from.GameplayTags.Select(t => t.Tag).ToHashSet(StringComparer.Ordinal);
        var toTags = to.GameplayTags.Select(t => t.Tag).ToHashSet(StringComparer.Ordinal);

        result.AddedTags.AddRange(toTags.Except(fromTags, StringComparer.Ordinal).OrderBy(x => x));
        result.RemovedTags.AddRange(fromTags.Except(toTags, StringComparer.Ordinal).OrderBy(x => x));

        if (result.AddedTags.Count > 0 || result.RemovedTags.Count > 0)
        {
            result.ChangedKinds.Add(UeKnowledgeSchema.ShardKinds.GameplayTags);
        }
    }

    private static void DiffTools(
        UeKnowledgeFactIndex from,
        UeKnowledgeFactIndex to,
        UeKnowledgeSemanticDiffResult result)
    {
        var fromMap = from.McpTools.ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);
        var toMap = to.McpTools.ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        foreach (var name in toMap.Keys.Except(fromMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            result.AddedToolNames.Add(name);
        }

        foreach (var name in fromMap.Keys.Except(toMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            result.RemovedToolNames.Add(name);
        }

        foreach (var name in fromMap.Keys.Intersect(toMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            var left = $"{fromMap[name].InputSchemaJson}|{fromMap[name].MinBuildChangelist}|{fromMap[name].MaxBuildChangelist}";
            var right = $"{toMap[name].InputSchemaJson}|{toMap[name].MinBuildChangelist}|{toMap[name].MaxBuildChangelist}";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                result.ChangedToolNames.Add(name);
            }
        }

        if (result.AddedToolNames.Count > 0
            || result.RemovedToolNames.Count > 0
            || result.ChangedToolNames.Count > 0)
        {
            result.ChangedKinds.Add(UeKnowledgeSchema.ShardKinds.McpContracts);
        }
    }

    private static void DiffAssets(
        UeKnowledgeFactIndex from,
        UeKnowledgeFactIndex to,
        UeKnowledgeSemanticDiffResult result)
    {
        var fromMap = from.PrimaryAssets.ToDictionary(
            a => a.StableId,
            a => SerializeAsset(a),
            StringComparer.Ordinal);
        var toMap = to.PrimaryAssets.ToDictionary(
            a => a.StableId,
            a => SerializeAsset(a),
            StringComparer.Ordinal);

        if (MapsDiffer(fromMap, toMap))
        {
            result.ChangedKinds.Add(UeKnowledgeSchema.ShardKinds.PrimaryAssets);
        }
    }

    private static void DiffSchemas(
        UeKnowledgeFactIndex from,
        UeKnowledgeFactIndex to,
        UeKnowledgeSemanticDiffResult result)
    {
        var fromMap = from.DataAssetSchemas.ToDictionary(
            s => s.StableId,
            s => SerializeSchema(s),
            StringComparer.Ordinal);
        var toMap = to.DataAssetSchemas.ToDictionary(
            s => s.StableId,
            s => SerializeSchema(s),
            StringComparer.Ordinal);

        if (MapsDiffer(fromMap, toMap))
        {
            result.ChangedKinds.Add(UeKnowledgeSchema.ShardKinds.DataSchemas);
        }
    }

    private static bool MapsDiffer(
        IReadOnlyDictionary<string, string> fromMap,
        IReadOnlyDictionary<string, string> toMap)
    {
        if (fromMap.Count != toMap.Count)
        {
            return true;
        }

        foreach (var (key, fromValue) in fromMap)
        {
            if (!toMap.TryGetValue(key, out var toValue)
                || !string.Equals(fromValue, toValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string SerializeAsset(UePrimaryAssetFact asset)
        => string.Join(
            "|",
            asset.StableId,
            asset.AssetType,
            asset.Name,
            asset.Bundle,
            string.Join(",", asset.ExplicitDependencies.OrderBy(d => d, StringComparer.Ordinal)));

    private static string SerializeSchema(UeDataAssetSchemaFact schema)
        => string.Join(
            "|",
            schema.StableId,
            schema.TypeName,
            schema.SuperType,
            string.Join(
                ",",
                schema.Fields
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => $"{f.Name}:{f.Type}:{f.IsArray}")));

    private static string SerializeClass(UeClassFact fact)
        => string.Join(
            "|",
            fact.Name,
            fact.SuperClass,
            fact.Source,
            fact.ModuleName,
            string.Join(",", fact.Properties.OrderBy(p => p.Name).Select(p => $"{p.Name}:{p.Type}")),
            string.Join(",", fact.Functions.OrderBy(f => f.Name).Select(f => f.Name)));
}
