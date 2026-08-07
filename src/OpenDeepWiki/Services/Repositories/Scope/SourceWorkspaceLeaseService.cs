using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories.Scope;

public static class WorkspaceLeasePurposes
{
    public const string Sync = "sync";
    public const string Generate = "generate";
    public const string Inventory = "inventory";
}

/// <summary>
/// 禁止同步与生成并发修改同一工作区。不自动 revert/清理本地文件。
/// </summary>
public interface ISourceWorkspaceLease
{
    Task<SourceWorkspaceLeaseHandle?> TryAcquireAsync(
        IContext context,
        string repositoryId,
        string purpose,
        string ownerId,
        string? ownerDescription = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        IContext context,
        string repositoryId,
        string ownerId,
        CancellationToken cancellationToken = default);

    Task<SourceWorkspaceLease?> GetActiveAsync(
        string repositoryId,
        CancellationToken cancellationToken = default);
}

public sealed class SourceWorkspaceLeaseHandle : IAsyncDisposable
{
    private readonly ISourceWorkspaceLease _leaseService;
    private readonly IContext _context;
    private readonly string _repositoryId;
    private readonly string _ownerId;
    private bool _released;

    public SourceWorkspaceLeaseHandle(
        ISourceWorkspaceLease leaseService,
        IContext context,
        string repositoryId,
        string ownerId,
        SourceWorkspaceLease lease)
    {
        _leaseService = leaseService;
        _context = context;
        _repositoryId = repositoryId;
        _ownerId = ownerId;
        Lease = lease;
    }

    public SourceWorkspaceLease Lease { get; }

    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        await _leaseService.ReleaseAsync(_context, _repositoryId, _ownerId);
    }
}

public sealed class SourceWorkspaceLeaseService(IContext rootContext) : ISourceWorkspaceLease
{
    public async Task<SourceWorkspaceLeaseHandle?> TryAcquireAsync(
        IContext context,
        string repositoryId,
        string purpose,
        string ownerId,
        string? ownerDescription = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        await ExpireStaleAsync(context, repositoryId, cancellationToken);

        var existing = await context.SourceWorkspaceLeases
            .FirstOrDefaultAsync(
                item => item.RepositoryId == repositoryId && !item.IsDeleted,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.OwnerId == ownerId)
            {
                return new SourceWorkspaceLeaseHandle(this, context, repositoryId, ownerId, existing);
            }

            return null;
        }

        var lease = new SourceWorkspaceLease
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            Purpose = purpose,
            OwnerId = ownerId,
            OwnerDescription = ownerDescription,
            AcquiredAt = DateTime.UtcNow,
            ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : null,
            CreatedAt = DateTime.UtcNow
        };

        context.SourceWorkspaceLeases.Add(lease);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new SourceWorkspaceLeaseHandle(this, context, repositoryId, ownerId, lease);
        }
        catch (DbUpdateException)
        {
            if (context is DbContext dbContext)
            {
                dbContext.Entry(lease).State = EntityState.Detached;
            }

            return null;
        }
    }

    public async Task ReleaseAsync(
        IContext context,
        string repositoryId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var lease = await context.SourceWorkspaceLeases
            .FirstOrDefaultAsync(
                item => item.RepositoryId == repositoryId
                        && item.OwnerId == ownerId
                        && !item.IsDeleted,
                cancellationToken);

        if (lease is null)
        {
            return;
        }

        // Hard-delete so the unique RepositoryId index can be re-acquired.
        context.SourceWorkspaceLeases.Remove(lease);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<SourceWorkspaceLease?> GetActiveAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        return rootContext.SourceWorkspaceLeases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.RepositoryId == repositoryId
                        && !item.IsDeleted
                        && (item.ExpiresAt == null || item.ExpiresAt > DateTime.UtcNow),
                cancellationToken);
    }

    private static async Task ExpireStaleAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var stale = await context.SourceWorkspaceLeases
            .Where(item => item.RepositoryId == repositoryId
                           && !item.IsDeleted
                           && item.ExpiresAt != null
                           && item.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var item in stale)
        {
            context.SourceWorkspaceLeases.Remove(item);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
