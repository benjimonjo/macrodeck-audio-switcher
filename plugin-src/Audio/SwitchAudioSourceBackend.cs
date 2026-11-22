namespace AudioSwitcher.Audio;

/// <summary>
/// macOS backend driven by <c>SwitchAudioSource</c> (<c>brew install switchaudio-osx</c>).
/// macOS moves running audio to the new default by itself, so <c>moveStreams</c> is not needed.
/// </summary>
internal sealed class SwitchAudioSourceBackend : IAudioBackend
{
    private readonly string? _tool = ProcessRunner.Find("SwitchAudioSource", "/opt/homebrew/bin", "/usr/local/bin");

    public TimeSpan PollInterval => TimeSpan.FromSeconds(3);

    private string Tool => _tool ?? throw new AudioBackendException(
        "SwitchAudioSource was not found. Install it with: brew install switchaudio-osx");

    public async Task<AudioSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        var outputs = await ListAsync("output", cancellationToken).ConfigureAwait(false);
        var inputs = await ListAsync("input", cancellationToken).ConfigureAwait(false);
        var currentOutput = await CurrentAsync("output", cancellationToken).ConfigureAwait(false);
        var currentInput = await CurrentAsync("input", cancellationToken).ConfigureAwait(false);
        return new AudioSnapshot(outputs, inputs, currentOutput, currentInput, null);
    }

    public async Task SetDefaultAsync(AudioDirection direction, string deviceId, bool moveStreams, CancellationToken cancellationToken)
    {
        var type = direction == AudioDirection.Output ? "output" : "input";
        var result = await ProcessRunner.RunAsync(Tool, ["-s", deviceId, "-t", type], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new AudioBackendException($"SwitchAudioSource failed: {(result.StandardError + result.StandardOutput).Trim()}");
        }
    }

    public Task WatchAsync(Action changed, CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken); // No change feed; the service polls instead.

    private async Task<IReadOnlyList<AudioDevice>> ListAsync(string type, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Tool, ["-a", "-t", type], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new AudioBackendException($"SwitchAudioSource could not list {type} devices: {result.StandardError.Trim()}");
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new AudioDevice(name, name))
            .ToList();
    }

    private async Task<string?> CurrentAsync(string type, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Tool, ["-c", "-t", type], cancellationToken).ConfigureAwait(false);
        var name = result.StandardOutput.Trim();
        return result.ExitCode == 0 && name.Length > 0 ? name : null;
    }
}

internal sealed class UnsupportedBackend(string reason) : IAudioBackend
{
    public TimeSpan PollInterval => TimeSpan.FromMinutes(10);

    public Task<AudioSnapshot> QueryAsync(CancellationToken cancellationToken) => Task.FromResult(AudioSnapshot.Failed(reason));

    public Task SetDefaultAsync(AudioDirection direction, string deviceId, bool moveStreams, CancellationToken cancellationToken)
        => throw new AudioBackendException(reason);

    public Task WatchAsync(Action changed, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
}
