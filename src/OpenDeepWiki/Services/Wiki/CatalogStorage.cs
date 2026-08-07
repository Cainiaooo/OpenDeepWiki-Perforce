using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Services.Wiki;

/// <summary>
/// Provides storage operations for wiki catalog structures.
/// When <paramref name="generationId"/> is set (or WikiGenerationContext has a staging id),
/// reads/writes are isolated to that generation so readers only see published content.
/// </summary>
public class CatalogStorage
{
    private readonly IContext _context;
    private readonly string _branchLanguageId;
    private readonly string _generationId;
    private readonly bool _isStagingWriter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of CatalogStorage for a specific branch language.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="branchLanguageId">The branch language ID to operate on.</param>
    /// <param name="generationId">
    /// Optional generation scope. When null, uses <see cref="WikiGenerationContext.CurrentGenerationId"/>
    /// if present; otherwise legacy empty generation for writers, or published generation for pure reads
    /// is resolved per operation when needed.
    /// </param>
    public CatalogStorage(IContext context, string branchLanguageId, string? generationId = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _branchLanguageId = branchLanguageId ?? throw new ArgumentNullException(nameof(branchLanguageId));

        var resolved = generationId ?? WikiGenerationContext.CurrentGenerationId;
        if (!string.IsNullOrEmpty(resolved))
        {
            _generationId = resolved;
            _isStagingWriter = true;
        }
        else
        {
            _generationId = WikiPublicationQuery.LegacyGenerationId;
            _isStagingWriter = false;
        }
    }

    public async Task<string> GetCatalogJsonAsync(CancellationToken cancellationToken = default)
    {
        var catalogs = await LoadVisibleCatalogsAsync(cancellationToken);
        var root = BuildCatalogTree(catalogs);
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    public async Task SetCatalogAsync(string catalogJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogJson))
        {
            throw new ArgumentException("Catalog JSON cannot be empty.", nameof(catalogJson));
        }

        var root = JsonSerializer.Deserialize<CatalogRoot>(catalogJson, JsonOptions);
        if (root == null)
        {
            throw new ArgumentException("Invalid catalog JSON format.", nameof(catalogJson));
        }

        // Only touch the active write generation — never soft-delete the published tree mid-staging.
        var existingCatalogs = await _context.DocCatalogs
            .Where(c => c.BranchLanguageId == _branchLanguageId
                        && c.GenerationId == _generationId
                        && !c.IsDeleted)
            .ToListAsync(cancellationToken);

        static int CountItems(List<CatalogItem> items) => items.Sum(i => 1 + CountItems(i.Children));
        var newCount = CountItems(root.Items);
        if (existingCatalogs.Count >= 10 && newCount < existingCatalogs.Count / 2)
        {
            throw new InvalidOperationException(
                $"Refusing to replace catalog: new structure has {newCount} items but {existingCatalogs.Count} exist. Use EditCatalog for partial updates.");
        }

        foreach (var catalog in existingCatalogs)
        {
            catalog.MarkAsDeleted();
        }

        await CreateCatalogItemsAsync(root.Items, null, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateNodeAsync(string path, string nodeJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(nodeJson))
        {
            throw new ArgumentException("Node JSON cannot be empty.", nameof(nodeJson));
        }

        var updatedItem = JsonSerializer.Deserialize<CatalogItem>(nodeJson, JsonOptions);
        if (updatedItem == null)
        {
            throw new ArgumentException("Invalid node JSON format.", nameof(nodeJson));
        }

        var existingCatalog = await FindInWriteGenerationAsync(path, cancellationToken);
        if (existingCatalog == null)
        {
            throw new InvalidOperationException($"Catalog node with path '{path}' not found.");
        }

        existingCatalog.Title = updatedItem.Title;
        existingCatalog.Order = updatedItem.Order;
        if (updatedItem.Children.Count > 0)
        {
            existingCatalog.DocFileId = null;
        }
        existingCatalog.UpdateTimestamp();

        if (updatedItem.Children.Count > 0)
        {
            var existingChildren = await _context.DocCatalogs
                .Where(c => c.ParentId == existingCatalog.Id
                            && c.GenerationId == _generationId
                            && !c.IsDeleted)
                .ToListAsync(cancellationToken);

            foreach (var child in existingChildren)
            {
                child.MarkAsDeleted();
            }

            await CreateCatalogItemsAsync(updatedItem.Children, existingCatalog.Id, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<CatalogItem?> GetNodeAsync(string path, CancellationToken cancellationToken = default)
    {
        var allCatalogs = await LoadVisibleCatalogsAsync(cancellationToken);
        var catalog = allCatalogs.FirstOrDefault(c => c.Path == path);
        if (catalog == null)
        {
            return null;
        }

        return BuildCatalogItemWithChildren(catalog, allCatalogs);
    }

    private async Task<List<DocCatalog>> LoadVisibleCatalogsAsync(CancellationToken cancellationToken)
    {
        if (_isStagingWriter)
        {
            return await _context.DocCatalogs
                .Where(c => c.BranchLanguageId == _branchLanguageId
                            && c.GenerationId == _generationId
                            && !c.IsDeleted)
                .OrderBy(c => c.Order)
                .ToListAsync(cancellationToken);
        }

        var publishedGenerationId = await WikiPublicationQuery.GetPublishedGenerationIdAsync(
            _context, _branchLanguageId, cancellationToken);

        return await WikiPublicationQuery
            .FilterVisibleCatalogs(_context.DocCatalogs.AsQueryable(), _branchLanguageId, publishedGenerationId)
            .OrderBy(c => c.Order)
            .ToListAsync(cancellationToken);
    }

    private Task<DocCatalog?> FindInWriteGenerationAsync(string path, CancellationToken cancellationToken)
    {
        return _context.DocCatalogs.FirstOrDefaultAsync(
            c => c.BranchLanguageId == _branchLanguageId
                 && c.Path == path
                 && c.GenerationId == _generationId
                 && !c.IsDeleted,
            cancellationToken);
    }

    private CatalogRoot BuildCatalogTree(List<DocCatalog> catalogs)
    {
        var root = new CatalogRoot();
        var rootItems = catalogs.Where(c => c.ParentId == null).OrderBy(c => c.Order);

        foreach (var item in rootItems)
        {
            root.Items.Add(BuildCatalogItemWithChildren(item, catalogs));
        }

        return root;
    }

    private CatalogItem BuildCatalogItemWithChildren(DocCatalog catalog, List<DocCatalog> allCatalogs)
    {
        var item = new CatalogItem
        {
            Title = catalog.Title,
            Path = catalog.Path,
            Order = catalog.Order,
            Children = new List<CatalogItem>()
        };

        var children = allCatalogs
            .Where(c => c.ParentId == catalog.Id)
            .OrderBy(c => c.Order);

        foreach (var child in children)
        {
            item.Children.Add(BuildCatalogItemWithChildren(child, allCatalogs));
        }

        return item;
    }

    private async Task CreateCatalogItemsAsync(List<CatalogItem> items, string? parentId, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var existingCatalog = await _context.DocCatalogs
                .FirstOrDefaultAsync(c => c.BranchLanguageId == _branchLanguageId
                                          && c.Path == item.Path
                                          && c.GenerationId == _generationId,
                    cancellationToken);

            string catalogId;
            if (existingCatalog != null)
            {
                existingCatalog.ParentId = parentId;
                existingCatalog.Title = item.Title;
                existingCatalog.Order = item.Order;
                existingCatalog.IsDeleted = false;
                existingCatalog.GenerationId = _generationId;
                if (item.Children.Count > 0)
                {
                    existingCatalog.DocFileId = null;
                }
                existingCatalog.UpdateTimestamp();
                catalogId = existingCatalog.Id;
            }
            else
            {
                var catalog = new DocCatalog
                {
                    Id = Guid.NewGuid().ToString(),
                    BranchLanguageId = _branchLanguageId,
                    ParentId = parentId,
                    Title = item.Title,
                    Path = item.Path,
                    Order = item.Order,
                    GenerationId = _generationId
                };

                _context.DocCatalogs.Add(catalog);
                catalogId = catalog.Id;
            }

            if (item.Children.Count > 0)
            {
                await CreateCatalogItemsAsync(item.Children, catalogId, cancellationToken);
            }
        }
    }
}
