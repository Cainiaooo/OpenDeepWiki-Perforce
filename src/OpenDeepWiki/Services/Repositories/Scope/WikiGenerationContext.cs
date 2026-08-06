namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// Async-local generation / selection context for wiki generation workers and tools.
/// </summary>
public static class WikiGenerationContext
{
    private static readonly AsyncLocal<string?> GenerationIdLocal = new();
    private static readonly AsyncLocal<IRepositoryFileSelectionPolicy?> PolicyLocal = new();

    public static string? CurrentGenerationId
    {
        get => GenerationIdLocal.Value;
        set => GenerationIdLocal.Value = value;
    }

    public static IRepositoryFileSelectionPolicy? SelectionPolicy
    {
        get => PolicyLocal.Value;
        set => PolicyLocal.Value = value;
    }

    public static void Clear()
    {
        GenerationIdLocal.Value = null;
        PolicyLocal.Value = null;
    }
}
