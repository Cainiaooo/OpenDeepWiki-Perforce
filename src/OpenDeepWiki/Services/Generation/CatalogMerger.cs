namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 按 scope/domain/page 稳定 ID 合并目录，而不是按模型输出顺序拼接。
/// </summary>
public interface ICatalogMerger
{
    MergedCatalogArtifact Merge(
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves);
}

public sealed class CatalogMerger : ICatalogMerger
{
    public MergedCatalogArtifact Merge(
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(leaves);

        var errors = new List<string>();
        var warnings = new List<string>();

        var pageById = manifests
            .GroupBy(item => item.PageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var duplicatePageIds = manifests
            .GroupBy(item => item.PageId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicate in duplicatePageIds)
        {
            errors.Add($"Duplicate page id: {duplicate}");
        }

        var leafStatus = leaves.ToDictionary(
            item => item.PageId,
            item => item.Status,
            StringComparer.OrdinalIgnoreCase);

        var scopeGroups = domains
            .GroupBy(domain => domain.ScopeId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roots = new List<MergedCatalogNode>();
        var order = 0;
        foreach (var scopeGroup in scopeGroups)
        {
            var scopeNode = new MergedCatalogNode
            {
                Id = $"scope:{scopeGroup.Key}",
                Title = scopeGroup.Key,
                Order = order++,
                Children = scopeGroup
                    .OrderBy(domain => domain.DomainId, StringComparer.OrdinalIgnoreCase)
                    .Select((domain, index) =>
                    {
                        var domainPages = manifests
                            .Where(manifest => string.Equals(
                                manifest.DomainId,
                                domain.DomainId,
                                StringComparison.OrdinalIgnoreCase))
                            .OrderBy(manifest => manifest.PageId, StringComparer.OrdinalIgnoreCase)
                            .Select((manifest, pageIndex) => new MergedCatalogNode
                            {
                                Id = manifest.PageId,
                                Title = BuildPageTitle(manifest, leafStatus),
                                PageId = manifest.PageId,
                                DomainId = domain.DomainId,
                                Order = pageIndex,
                                Children = []
                            })
                            .ToArray();

                        if (domainPages.Length == 0)
                        {
                            warnings.Add($"Empty domain without pages: {domain.DomainId}");
                        }

                        return new MergedCatalogNode
                        {
                            Id = domain.DomainId,
                            Title = domain.DisplayName,
                            DomainId = domain.DomainId,
                            Order = index,
                            Children = domainPages
                        };
                    })
                    .ToArray()
            };

            // 去掉无意义单子节点分组：若 scope 下只有一个 domain 且该 domain 只有一页，提升页面
            if (scopeNode.Children.Count == 1 && scopeNode.Children[0].Children.Count == 1)
            {
                var onlyPage = scopeNode.Children[0].Children[0];
                roots.Add(new MergedCatalogNode
                {
                    Id = onlyPage.Id,
                    Title = onlyPage.Title,
                    PageId = onlyPage.PageId,
                    DomainId = onlyPage.DomainId,
                    Order = scopeNode.Order,
                    Children = []
                });
            }
            else
            {
                roots.Add(scopeNode);
            }
        }

        // related page 环检测（简单 DFS）
        foreach (var manifest in pageById.Values)
        {
            if (HasCycle(manifest.PageId, pageById, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                warnings.Add($"Potential relatedPages cycle involving {manifest.PageId}");
            }
        }

        return new MergedCatalogArtifact
        {
            Roots = roots,
            ValidationErrors = errors,
            ValidationWarnings = warnings
        };
    }

    private static string BuildPageTitle(
        ScopeManifest manifest,
        IReadOnlyDictionary<string, LeafPageStatus> leafStatus)
    {
        var title = manifest.TopicSummary;
        if (title.Length > 80)
        {
            title = title[..77] + "...";
        }

        if (leafStatus.TryGetValue(manifest.PageId, out var status) && status == LeafPageStatus.Failed)
        {
            return $"[failed] {title}";
        }

        return title;
    }

    private static bool HasCycle(
        string pageId,
        IReadOnlyDictionary<string, ScopeManifest> pages,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(pageId))
        {
            return false;
        }

        if (!visiting.Add(pageId))
        {
            return true;
        }

        if (pages.TryGetValue(pageId, out var manifest))
        {
            foreach (var related in manifest.RelatedPages)
            {
                if (pages.ContainsKey(related) && HasCycle(related, pages, visiting, visited))
                {
                    return true;
                }
            }
        }

        visiting.Remove(pageId);
        visited.Add(pageId);
        return false;
    }
}
