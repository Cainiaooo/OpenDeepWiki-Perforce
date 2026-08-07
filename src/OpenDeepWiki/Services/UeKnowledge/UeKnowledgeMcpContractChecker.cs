namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgeMcpContractChecker
{
    /// <summary>
    /// 检查请求的 Build CL 与知识包中的 MCP 工具契约是否兼容。
    /// </summary>
    UeMcpContractCompatibilityResult Check(
        UeKnowledgeFactIndex index,
        string requestBuildChangelist,
        IReadOnlyCollection<string>? requiredToolNames = null);
}

public sealed class UeKnowledgeMcpContractChecker : IUeKnowledgeMcpContractChecker
{
    public UeMcpContractCompatibilityResult Check(
        UeKnowledgeFactIndex index,
        string requestBuildChangelist,
        IReadOnlyCollection<string>? requiredToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(index);

        var result = new UeMcpContractCompatibilityResult
        {
            BuildChangelist = requestBuildChangelist?.Trim() ?? string.Empty,
            PackageBuildChangelist = index.BuildChangelist
        };

        if (string.IsNullOrWhiteSpace(result.BuildChangelist))
        {
            result.Warnings.Add("requestBuildChangelist 为空，仅校验工具是否存在");
        }
        else if (!string.Equals(result.BuildChangelist, index.BuildChangelist, StringComparison.Ordinal))
        {
            // Build CL 不同不一定不兼容；按工具级 min/max 再判断
            result.Warnings.Add(
                $"请求 Build CL {result.BuildChangelist} 与包 Build CL {index.BuildChangelist} 不同，将按工具级范围判断");
        }

        var toolMap = index.McpTools.ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);
        var required = requiredToolNames is { Count: > 0 }
            ? requiredToolNames
            : toolMap.Keys.ToArray();

        foreach (var toolName in required.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!toolMap.TryGetValue(toolName, out var contract))
            {
                result.MissingTools.Add(toolName);
                continue;
            }

            if (!IsToolCompatibleWithBuild(contract, result.BuildChangelist, out var reason))
            {
                result.IncompatibleTools.Add($"{toolName}: {reason}");
            }
        }

        result.IsCompatible = result.MissingTools.Count == 0 && result.IncompatibleTools.Count == 0;
        return result;
    }

    private static bool IsToolCompatibleWithBuild(
        UeMcpToolContract contract,
        string requestBuildChangelist,
        out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(requestBuildChangelist))
        {
            return true;
        }

        if (!TryParseCl(requestBuildChangelist, out var requestCl))
        {
            // 非数字 CL（如 git sha）时，仅在 min/max 为空时兼容
            if (string.IsNullOrWhiteSpace(contract.MinBuildChangelist)
                && string.IsNullOrWhiteSpace(contract.MaxBuildChangelist))
            {
                return true;
            }

            reason = "无法解析请求 Build CL，且工具声明了 CL 范围";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(contract.MinBuildChangelist)
            && TryParseCl(contract.MinBuildChangelist, out var min)
            && requestCl < min)
        {
            reason = $"请求 CL {requestCl} < min {min}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(contract.MaxBuildChangelist)
            && TryParseCl(contract.MaxBuildChangelist, out var max)
            && requestCl > max)
        {
            reason = $"请求 CL {requestCl} > max {max}";
            return false;
        }

        return true;
    }

    private static bool TryParseCl(string value, out long cl)
        => long.TryParse(value.Trim(), out cl);
}
