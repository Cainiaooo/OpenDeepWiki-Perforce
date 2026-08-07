using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 确定性 Source Inventory 构建器。
/// 相同 snapshot + config 产生稳定排序与摘要；列事实不决定最终 Wiki 页面。
/// </summary>
public interface ISourceInventoryBuilder
{
    SourceInventory Build(GenerationRequest request);
}

public sealed class SourceInventoryBuilder : ISourceInventoryBuilder
{
    private static readonly Regex PublicDependencyRegex = new(
        @"PublicDependencyModuleNames\s*\.\s*AddRange\s*\(\s*new\s+string\s*\[\s*\]\s*\{([^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PrivateDependencyRegex = new(
        @"PrivateDependencyModuleNames\s*\.\s*AddRange\s*\(\s*new\s+string\s*\[\s*\]\s*\{([^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex DependencyNameRegex = new(
        @"""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SourceInventory Build(GenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");
        }

        var policy = new RepositoryFileSelectionPolicy(
            request.ResolvedScopes,
            workingDirectory);

        var warnings = new List<string>();
        var files = new List<InventoryFileEntry>();
        var projects = new List<InventoryProjectEntry>();
        var plugins = new List<InventoryPluginEntry>();
        var modules = new List<InventoryModuleEntry>();
        var targets = new List<InventoryTargetEntry>();

        // 1) 枚举 Document 候选
        // AllowedTrackedPaths：null = 允许磁盘扫描；非 null（含空集合）= 严格 allow-list。
        // ManifestByPath 存在且未显式传 allow-list 时，以其 keys 作为 allow-list。
        var effectiveAllowList = request.AllowedTrackedPaths;
        if (effectiveAllowList is null && request.ManifestByPath is { Count: > 0 })
        {
            effectiveAllowList = new HashSet<string>(
                request.ManifestByPath.Keys.Select(ScopePathUtility.NormalizeRelativePath),
                StringComparer.OrdinalIgnoreCase);
        }

        if (effectiveAllowList is null
            && request.ResolvedScopes.WorkspaceContentPolicy == WorkspaceContentPolicy.SubmittedHaveOnly)
        {
            warnings.Add(
                "SubmittedHaveOnly inventory ran without AllowedTrackedPaths/ManifestByPath; " +
                "disk enumeration may include untracked or opened local files.");
        }

        var documentPaths = EnumerateDocumentPaths(
            workingDirectory,
            policy,
            effectiveAllowList,
            request.ManifestByPath,
            warnings);

        // 2) 识别结构信号
        foreach (var relativePath in documentPaths)
        {
            var metadata = ToSourceMetadata(relativePath, request.ManifestByPath);
            var scopeDecision = policy.EvaluateDocumentCandidate(relativePath, metadata);
            if (!scopeDecision.Accepted)
            {
                continue;
            }

            var scopeId = scopeDecision.MatchedScopeId ?? "unknown";
            var fullPath = Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                warnings.Add($"Inventory path missing on disk: {relativePath}");
                continue;
            }

            var suffix = ScopePathUtility.FindLongestMatchingSuffix(
                relativePath,
                ScopeDefaults.DefaultDocumentSuffixes) ?? Path.GetExtension(relativePath);

            string? haveRevision = null;
            string? contentDigest = null;
            if (request.ManifestByPath is not null
                && request.ManifestByPath.TryGetValue(relativePath, out var manifestEntry))
            {
                haveRevision = manifestEntry.HaveRevision;
                contentDigest = manifestEntry.LocalDigest;
            }

            contentDigest ??= ComputeContentDigest(fullPath);

            var fileKind = ClassifyFileKind(relativePath, suffix);
            var isEntryPoint = fileKind is "BuildCs" or "TargetCs" or "UProject" or "UPlugin";

            files.Add(new InventoryFileEntry
            {
                RelativePath = relativePath,
                ScopeId = scopeId,
                FileKind = fileKind,
                Suffix = suffix,
                SizeBytes = fileInfo.Length,
                ContentDigest = contentDigest,
                HaveRevision = haveRevision,
                IsEntryPoint = isEntryPoint
            });

            if (fileKind == "UProject")
            {
                projects.Add(CreateProjectEntry(relativePath, scopeId));
            }
            else if (fileKind == "UPlugin")
            {
                plugins.Add(CreatePluginEntry(relativePath, scopeId));
            }
            else if (fileKind == "BuildCs")
            {
                modules.Add(CreateModuleEntry(relativePath, scopeId, fullPath));
            }
            else if (fileKind == "TargetCs")
            {
                targets.Add(CreateTargetEntry(relativePath, scopeId));
            }
        }

        // 稳定排序
        files = files
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        projects = projects
            .OrderBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        plugins = plugins
            .OrderBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        modules = modules
            .OrderBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        targets = targets
            .OrderBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 3) 关联 module/plugin/project 与文件
        AttachStructureIds(files, modules, plugins, projects);

        // 4) 补全 module 文件计数与 Public/Private 信号
        modules = modules
            .Select(module => EnrichModule(module, files, workingDirectory))
            .OrderBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        plugins = plugins
            .Select(plugin => new InventoryPluginEntry
            {
                PluginId = plugin.PluginId,
                Name = plugin.Name,
                RootPath = plugin.RootPath,
                ScopeId = plugin.ScopeId,
                UpluginPath = plugin.UpluginPath,
                ModuleIds = modules
                    .Where(module => string.Equals(module.PluginId, plugin.PluginId, StringComparison.OrdinalIgnoreCase))
                    .Select(module => module.ModuleId)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .OrderBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dependencies = modules
            .SelectMany(module =>
                module.PublicDependencies.Select(dep => new ModuleDependencyEdge
                {
                    FromModuleId = module.ModuleId,
                    ToModuleName = dep,
                    Kind = "Public"
                })
                .Concat(module.PrivateDependencies.Select(dep => new ModuleDependencyEdge
                {
                    FromModuleId = module.ModuleId,
                    ToModuleName = dep,
                    Kind = "Private"
                })))
            .OrderBy(edge => edge.FromModuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.ToModuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshotIdentity = request.Snapshot.ToStableString();
        var contentHash = ComputeInventoryHash(
            snapshotIdentity,
            request.ResolvedScopes.ContentHash,
            files,
            modules,
            plugins,
            projects,
            targets,
            dependencies);

        return new SourceInventory
        {
            SnapshotIdentity = snapshotIdentity,
            ContentHash = contentHash,
            WorkingDirectory = workingDirectory,
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Files = files,
            Modules = modules,
            Plugins = plugins,
            Projects = projects,
            Targets = targets,
            Dependencies = dependencies,
            Warnings = warnings
        };
    }

    private static List<string> EnumerateDocumentPaths(
        string workingDirectory,
        IRepositoryFileSelectionPolicy policy,
        IReadOnlySet<string>? allowedTrackedPaths,
        IReadOnlyDictionary<string, WorkspaceManifestEntry>? manifestByPath,
        List<string> warnings)
    {
        // 显式 allow-list（含空集合）不得回退到全盘扫描。
        if (allowedTrackedPaths is not null)
        {
            return allowedTrackedPaths
                .Select(ScopePathUtility.NormalizeRelativePath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path =>
                {
                    var metadata = ToSourceMetadata(path, manifestByPath);
                    return policy.IsDocumentCandidate(path, metadata);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var results = new List<string>();
        void Walk(string directoryFullPath)
        {
            string relativeDir;
            try
            {
                relativeDir = Path.GetRelativePath(workingDirectory, directoryFullPath).Replace('\\', '/');
                if (relativeDir == ".")
                {
                    relativeDir = string.Empty;
                }
            }
            catch
            {
                return;
            }

            if (!string.IsNullOrEmpty(relativeDir)
                && policy.ShouldPruneDirectory(relativeDir, FileSelectionOperation.DocumentCandidate))
            {
                return;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directoryFullPath);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to enumerate '{relativeDir}': {ex.Message}");
                return;
            }

            foreach (var entry in entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var attr = File.GetAttributes(entry);
                    if (attr.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // 不跟随符号链接，避免逃逸
                        continue;
                    }

                    if (attr.HasFlag(FileAttributes.Directory))
                    {
                        Walk(entry);
                        continue;
                    }

                    var relative = ScopePathUtility.NormalizeRelativePath(
                        Path.GetRelativePath(workingDirectory, entry).Replace('\\', '/'));
                    var metadata = ToSourceMetadata(relative, manifestByPath);
                    if (policy.IsDocumentCandidate(relative, metadata))
                    {
                        results.Add(relative);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to inspect entry: {ex.Message}");
                }
            }
        }

        Walk(workingDirectory);
        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SourceFileMetadata? ToSourceMetadata(
        string relativePath,
        IReadOnlyDictionary<string, WorkspaceManifestEntry>? manifestByPath)
    {
        if (manifestByPath is null || manifestByPath.Count == 0)
        {
            return null;
        }

        if (!TryGetManifestEntry(manifestByPath, relativePath, out var entry))
        {
            // Manifest 作为完整事实源时，未列入的路径按未跟踪拒绝。
            return new SourceFileMetadata
            {
                IsTracked = false
            };
        }

        return new SourceFileMetadata
        {
            DepotPath = entry.DepotPath,
            FileType = entry.FileType,
            IsTracked = true,
            IsOpened = entry.IsOpened,
            OpenedAction = entry.OpenedAction,
            MatchesHaveContent = entry.MatchesHaveContent,
            LocalDigest = entry.LocalDigest,
            SizeBytes = null
        };
    }

    private static bool TryGetManifestEntry(
        IReadOnlyDictionary<string, WorkspaceManifestEntry> manifestByPath,
        string relativePath,
        out WorkspaceManifestEntry entry)
    {
        var normalized = ScopePathUtility.NormalizeRelativePath(relativePath);
        if (manifestByPath.TryGetValue(normalized, out entry!))
        {
            return true;
        }

        if (manifestByPath.TryGetValue(relativePath, out entry!))
        {
            return true;
        }

        foreach (var pair in manifestByPath)
        {
            if (string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
            {
                entry = pair.Value;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    private static string ClassifyFileKind(string relativePath, string? suffix)
    {
        var fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "BuildCs";
        }

        if (fileName.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "TargetCs";
        }

        if (fileName.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
        {
            return "UProject";
        }

        if (fileName.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase))
        {
            return "UPlugin";
        }

        if (string.Equals(suffix, ".as", StringComparison.OrdinalIgnoreCase))
        {
            return "AngelScript";
        }

        if (string.Equals(suffix, ".h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".hpp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".inl", StringComparison.OrdinalIgnoreCase))
        {
            return "Header";
        }

        if (string.Equals(suffix, ".cpp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".c", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".cc", StringComparison.OrdinalIgnoreCase))
        {
            return "Source";
        }

        if (string.Equals(suffix, ".ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            return "Config";
        }

        if (string.Equals(suffix, ".md", StringComparison.OrdinalIgnoreCase))
        {
            return "Document";
        }

        return "Other";
    }

    private static InventoryProjectEntry CreateProjectEntry(string uprojectPath, string scopeId)
    {
        var name = Path.GetFileNameWithoutExtension(uprojectPath);
        var root = ScopePathUtility.NormalizeRoot(Path.GetDirectoryName(uprojectPath)?.Replace('\\', '/') ?? string.Empty);
        var projectId = StableId("project", scopeId, root, name);
        return new InventoryProjectEntry
        {
            ProjectId = projectId,
            Name = name,
            RootPath = root,
            ScopeId = scopeId,
            UprojectPath = uprojectPath
        };
    }

    private static InventoryPluginEntry CreatePluginEntry(string upluginPath, string scopeId)
    {
        var name = Path.GetFileNameWithoutExtension(upluginPath);
        var root = ScopePathUtility.NormalizeRoot(Path.GetDirectoryName(upluginPath)?.Replace('\\', '/') ?? string.Empty);
        var pluginId = StableId("plugin", scopeId, root, name);
        return new InventoryPluginEntry
        {
            PluginId = pluginId,
            Name = name,
            RootPath = root,
            ScopeId = scopeId,
            UpluginPath = upluginPath,
            ModuleIds = []
        };
    }

    private static InventoryModuleEntry CreateModuleEntry(
        string buildCsPath,
        string scopeId,
        string fullPath)
    {
        var name = Path.GetFileName(buildCsPath);
        if (name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".Build.cs".Length];
        }

        var root = ScopePathUtility.NormalizeRoot(
            Path.GetDirectoryName(buildCsPath)?.Replace('\\', '/') ?? string.Empty);
        var moduleId = StableId("module", scopeId, root, name);
        var (publicDeps, privateDeps) = ParseDependencies(fullPath);

        return new InventoryModuleEntry
        {
            ModuleId = moduleId,
            Name = name,
            RootPath = root,
            ScopeId = scopeId,
            BuildCsPath = buildCsPath,
            PublicDependencies = publicDeps,
            PrivateDependencies = privateDeps
        };
    }

    private static InventoryTargetEntry CreateTargetEntry(string targetCsPath, string scopeId)
    {
        var name = Path.GetFileName(targetCsPath);
        if (name.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".Target.cs".Length];
        }

        var targetId = StableId("target", scopeId, targetCsPath, name);
        return new InventoryTargetEntry
        {
            TargetId = targetId,
            Name = name,
            TargetCsPath = targetCsPath,
            ScopeId = scopeId
        };
    }

    private static (IReadOnlyList<string> Public, IReadOnlyList<string> Private) ParseDependencies(string buildCsFullPath)
    {
        try
        {
            var text = File.ReadAllText(buildCsFullPath);
            return (ExtractDependencyNames(PublicDependencyRegex, text),
                ExtractDependencyNames(PrivateDependencyRegex, text));
        }
        catch
        {
            return ([], []);
        }
    }

    private static IReadOnlyList<string> ExtractDependencyNames(Regex blockRegex, string text)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in blockRegex.Matches(text))
        {
            if (!block.Success || block.Groups.Count < 2)
            {
                continue;
            }

            foreach (Match nameMatch in DependencyNameRegex.Matches(block.Groups[1].Value))
            {
                if (nameMatch.Success && nameMatch.Groups.Count > 1)
                {
                    names.Add(nameMatch.Groups[1].Value.Trim());
                }
            }
        }

        return names.ToArray();
    }

    private static void AttachStructureIds(
        List<InventoryFileEntry> files,
        List<InventoryModuleEntry> modules,
        List<InventoryPluginEntry> plugins,
        List<InventoryProjectEntry> projects)
    {
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var module = modules
                .Where(item => IsUnderOrEqual(file.RelativePath, item.RootPath))
                .OrderByDescending(item => item.RootPath.Length)
                .FirstOrDefault();
            var plugin = plugins
                .Where(item => IsUnderOrEqual(file.RelativePath, item.RootPath))
                .OrderByDescending(item => item.RootPath.Length)
                .FirstOrDefault();
            var project = projects
                .Where(item => IsUnderOrEqual(file.RelativePath, item.RootPath))
                .OrderByDescending(item => item.RootPath.Length)
                .FirstOrDefault();

            files[i] = new InventoryFileEntry
            {
                RelativePath = file.RelativePath,
                ScopeId = file.ScopeId,
                FileKind = file.FileKind,
                Suffix = file.Suffix,
                SizeBytes = file.SizeBytes,
                ContentDigest = file.ContentDigest,
                HaveRevision = file.HaveRevision,
                ModuleId = module?.ModuleId,
                PluginId = plugin?.PluginId,
                ProjectId = project?.ProjectId,
                IsEntryPoint = file.IsEntryPoint
            };
        }

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var plugin = plugins
                .Where(item => IsUnderOrEqual(module.RootPath, item.RootPath))
                .OrderByDescending(item => item.RootPath.Length)
                .FirstOrDefault();
            var project = projects
                .Where(item => IsUnderOrEqual(module.RootPath, item.RootPath))
                .OrderByDescending(item => item.RootPath.Length)
                .FirstOrDefault();

            modules[i] = new InventoryModuleEntry
            {
                ModuleId = module.ModuleId,
                Name = module.Name,
                RootPath = module.RootPath,
                ScopeId = module.ScopeId,
                BuildCsPath = module.BuildCsPath,
                PluginId = plugin?.PluginId,
                ProjectId = project?.ProjectId,
                HasPublic = module.HasPublic,
                HasPrivate = module.HasPrivate,
                FileCount = module.FileCount,
                PublicDependencies = module.PublicDependencies,
                PrivateDependencies = module.PrivateDependencies
            };
        }
    }

    private static InventoryModuleEntry EnrichModule(
        InventoryModuleEntry module,
        IReadOnlyList<InventoryFileEntry> files,
        string workingDirectory)
    {
        var moduleFiles = files
            .Where(file => string.Equals(file.ModuleId, module.ModuleId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var publicDir = Path.Combine(
            workingDirectory,
            module.RootPath.Replace('/', Path.DirectorySeparatorChar),
            "Public");
        var privateDir = Path.Combine(
            workingDirectory,
            module.RootPath.Replace('/', Path.DirectorySeparatorChar),
            "Private");

        return new InventoryModuleEntry
        {
            ModuleId = module.ModuleId,
            Name = module.Name,
            RootPath = module.RootPath,
            ScopeId = module.ScopeId,
            BuildCsPath = module.BuildCsPath,
            PluginId = module.PluginId,
            ProjectId = module.ProjectId,
            HasPublic = Directory.Exists(publicDir),
            HasPrivate = Directory.Exists(privateDir),
            FileCount = moduleFiles.Count,
            PublicDependencies = module.PublicDependencies,
            PrivateDependencies = module.PrivateDependencies
        };
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        var normalizedPath = ScopePathUtility.NormalizeRelativePath(path);
        var normalizedRoot = ScopePathUtility.NormalizeRoot(root);
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return true;
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StableId(string kind, string scopeId, string root, string name)
    {
        var raw = $"{kind}|{scopeId}|{ScopePathUtility.NormalizeRoot(root)}|{name}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..12];
        var slug = Slugify(name);
        return $"{kind}-{slug}-{hash}";
    }

    internal static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "item";
        }

        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "item" : slug;
    }

    private static string ComputeContentDigest(string fullPath)
    {
        try
        {
            // 全量内容摘要：后半部分等长修改也必须改变 digest，供增量重映射与缓存使用。
            using var stream = File.OpenRead(fullPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            try
            {
                var length = new FileInfo(fullPath).Length;
                return $"unreadable:{length}";
            }
            catch
            {
                return "unreadable";
            }
        }
    }

    private static string ComputeInventoryHash(
        string snapshotIdentity,
        string scopeHash,
        IReadOnlyList<InventoryFileEntry> files,
        IReadOnlyList<InventoryModuleEntry> modules,
        IReadOnlyList<InventoryPluginEntry> plugins,
        IReadOnlyList<InventoryProjectEntry> projects,
        IReadOnlyList<InventoryTargetEntry> targets,
        IReadOnlyList<ModuleDependencyEdge> dependencies)
    {
        var payload = new
        {
            snapshotIdentity,
            scopeHash,
            files = files.Select(file => new
            {
                file.RelativePath,
                file.ScopeId,
                file.FileKind,
                file.ContentDigest,
                file.HaveRevision,
                file.ModuleId,
                file.PluginId,
                file.ProjectId
            }),
            modules = modules.Select(module => new
            {
                module.ModuleId,
                module.Name,
                module.RootPath,
                module.BuildCsPath,
                module.PublicDependencies,
                module.PrivateDependencies
            }),
            plugins = plugins.Select(plugin => new
            {
                plugin.PluginId,
                plugin.Name,
                plugin.RootPath,
                plugin.UpluginPath
            }),
            projects = projects.Select(project => new
            {
                project.ProjectId,
                project.Name,
                project.RootPath,
                project.UprojectPath
            }),
            targets = targets.Select(target => new
            {
                target.TargetId,
                target.Name,
                target.TargetCsPath
            }),
            dependencies = dependencies.Select(edge => new
            {
                edge.FromModuleId,
                edge.ToModuleName,
                edge.Kind
            })
        };

        var json = JsonSerializer.Serialize(payload, HashJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
