using System.ComponentModel;
using System.Diagnostics;

namespace AudioSwitcher.Audio;

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Keep pactl's text output in English so the fallback parser can read it.
        info.Environment["LC_ALL"] = "C";
        return info;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new AudioBackendException($"'{fileName}' could not be started: {ex.Message}", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new AudioBackendException($"'{fileName}' did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>Resolves a command from PATH or a list of well-known folders (GUI apps often get a short PATH).</summary>
    public static string? Find(string command, params string[] extraFolders)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(extraFolders);
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(folder, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
