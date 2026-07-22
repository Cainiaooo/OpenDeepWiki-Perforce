using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace OpenDeepWiki.Services.Repositories.Perforce;

public sealed class PerforceCommandRunner(
    IOptionsMonitor<PerforceOptions> optionsMonitor,
    ILogger<PerforceCommandRunner> logger) : IPerforceCommandRunner
{
    public async Task<PerforceCommandResult> RunTaggedAsync(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            throw new PerforceCommandException($"Perforce workspace does not exist: {workspaceRoot}");
        }

        var options = optionsMonitor.CurrentValue;
        var attempts = Math.Max(1, options.MaxRetryAttempts + 1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var result = await RunOnceAsync(workspaceRoot, arguments, options, cancellationToken);
                if (result.IsSuccess || !IsTransientFailure(result.StandardOutput, result.StandardError))
                {
                    return result;
                }

                if (attempt == attempts)
                {
                    return result;
                }

                logger.LogWarning(
                    "Transient Perforce command failure; retrying. Command: {Command}, Attempt: {Attempt}/{Attempts}",
                    arguments.FirstOrDefault() ?? "unknown", attempt, attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PerforceCommandException ex) when (ex.IsTransient && attempt < attempts)
            {
                logger.LogWarning(
                    ex,
                    "Transient Perforce command exception; retrying. Command: {Command}, Attempt: {Attempt}/{Attempts}",
                    arguments.FirstOrDefault() ?? "unknown", attempt, attempts);
            }

            var delay = Math.Max(0, options.RetryBaseDelayMs) * (1 << Math.Min(attempt - 1, 10));
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new PerforceCommandException("Perforce command retry loop ended unexpectedly");
    }

    private static async Task<PerforceCommandResult> RunOnceAsync(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        PerforceOptions options,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(workspaceRoot, arguments, options)
        };

        try
        {
            if (!process.Start())
            {
                throw new PerforceCommandException("Failed to start the Perforce CLI");
            }
        }
        catch (Win32Exception ex)
        {
            throw new PerforceCommandException(
                $"Unable to start Perforce CLI '{options.Command}'. Verify that p4 is installed and configured.",
                innerException: ex);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.CommandTimeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            return new PerforceCommandResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainReadTasksAsync(standardOutputTask, standardErrorTask);
            throw new PerforceCommandException(
                $"Perforce command timed out after {Math.Max(1, options.CommandTimeoutSeconds)} seconds",
                isTransient: true);
        }
        catch
        {
            TryKill(process);
            await DrainReadTasksAsync(standardOutputTask, standardErrorTask);
            throw;
        }
    }

    private static async Task DrainReadTasksAsync(Task<string> standardOutputTask, Task<string> standardErrorTask)
    {
        // Best-effort: after kill, await readers so pipe buffers drain and exceptions stay observed.
        try
        {
            await Task.WhenAll(IgnoreReadFaultsAsync(standardOutputTask), IgnoreReadFaultsAsync(standardErrorTask));
        }
        catch
        {
            // Original command failure remains authoritative.
        }
    }

    private static async Task IgnoreReadFaultsAsync(Task<string> readTask)
    {
        try
        {
            await readTask;
        }
        catch
        {
            // Swallow cancel/IO faults from killed processes.
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        PerforceOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.Command) ? "p4" : options.Command,
            WorkingDirectory = Path.GetFullPath(workspaceRoot),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-ztag");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        SetEnvironment(startInfo, "P4PORT", options.Port);
        SetEnvironment(startInfo, "P4USER", options.User);
        SetEnvironment(startInfo, "P4CLIENT", options.Client);
        SetEnvironment(startInfo, "P4TICKETS", options.TicketsFile);
        SetEnvironment(startInfo, "P4CONFIG", options.ConfigFile);
        return startInfo;
    }

    private static void SetEnvironment(ProcessStartInfo startInfo, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static bool IsTransientFailure(string standardOutput, string standardError)
    {
        var combined = string.Concat(standardOutput, "\n", standardError);
        return combined.Contains("connect", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("network", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("TCP", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("WSA", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("partner exited", StringComparison.OrdinalIgnoreCase)
               || combined.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only. The original command failure remains authoritative.
        }
    }
}
