using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AudioSwitcher.Audio;

/// <summary>
/// Linux backend for PipeWire (through pipewire-pulse) and PulseAudio, driven by the <c>pactl</c> CLI.
/// </summary>
internal sealed class PactlBackend : IAudioBackend
{
    private static readonly string[] ExtraFolders = ["/usr/bin", "/usr/local/bin", "/bin"];

    private readonly string? _pactl = ProcessRunner.Find("pactl", ExtraFolders);
    private readonly string? _parec = ProcessRunner.Find("parec", ExtraFolders);
    private bool _jsonUnsupported;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(30);

    private string Pactl => _pactl ?? throw new AudioBackendException(
        "pactl was not found. Install it (Fedora: pulseaudio-utils, Debian/Ubuntu: pulseaudio-utils, Arch: libpulse).");

    public async Task<AudioSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        var sinks = await ListAsync("sinks", cancellationToken).ConfigureAwait(false);
        var sources = await ListAsync("sources", cancellationToken).ConfigureAwait(false);
        var (defaultSink, defaultSource) = await DefaultsAsync(cancellationToken).ConfigureAwait(false);

        return new AudioSnapshot(
            sinks.Where(d => !d.IsMonitor).Select(d => d.Device).ToList(),
            sources.Where(d => !d.IsMonitor).Select(d => d.Device).ToList(),
            defaultSink,
            defaultSource,
            null);
    }

    public async Task SetDefaultAsync(AudioDirection direction, string deviceId, bool moveStreams, CancellationToken cancellationToken)
    {
        var isOutput = direction == AudioDirection.Output;
        await RunCheckedAsync([isOutput ? "set-default-sink" : "set-default-source", deviceId], cancellationToken)
            .ConfigureAwait(false);

        if (!moveStreams)
        {
            return;
        }

        // PipeWire already moves streams that follow the default; PulseAudio does not. Move them
        // explicitly so the switch is heard straight away, but never move a recording that listens to a
        // monitor (desktop-audio capture in OBS, for example) onto a microphone.
        var monitorIndexes = isOutput
            ? new HashSet<string>()
            : (await ListAsync("sources", cancellationToken).ConfigureAwait(false))
                .Where(d => d.IsMonitor).Select(d => d.Index).ToHashSet();

        var streams = await ProcessRunner.RunAsync(
            Pactl, ["list", "short", isOutput ? "sink-inputs" : "source-outputs"], cancellationToken).ConfigureAwait(false);
        if (streams.ExitCode != 0)
        {
            return;
        }

        foreach (var line in streams.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length < 2 || monitorIndexes.Contains(columns[1]))
            {
                continue;
            }

            // Best effort: a stream can disappear between listing and moving.
            await ProcessRunner.RunAsync(
                Pactl, [isOutput ? "move-sink-input" : "move-source-output", columns[0], deviceId], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task WatchAsync(Action changed, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = ProcessRunner.CreateStartInfo(Pactl, ["subscribe"]),
            };
            try
            {
                process.Start();
                using var registration = cancellationToken.Register(() => ProcessRunner.TryKill(process));
                while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    // e.g. "Event 'change' on sink #56", "Event 'new' on source #80", "Event 'change' on server #-1"
                    if (line.Contains(" on sink #", StringComparison.Ordinal)
                        || line.Contains(" on source #", StringComparison.Ordinal)
                        || line.Contains(" on server", StringComparison.Ordinal)
                        || line.Contains(" on card #", StringComparison.Ordinal))
                    {
                        changed();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // pactl missing; polling still reports the problem on the widget.
            }
            finally
            {
                ProcessRunner.TryKill(process);
            }

            // The audio server restarted or pactl quit: refresh, then reconnect after a pause.
            changed();
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    public IDisposable? StartMeter(AudioDirection direction, AudioDevice device, Action<float> peak)
    {
        if (_parec is not { } parec)
        {
            return null;
        }

        // Recording from a Bluetooth microphone can switch a headset into its low-quality call profile,
        // so Bluetooth inputs get no meter. (Metering a Bluetooth output only listens to its monitor.)
        if (direction == AudioDirection.Input && device.Id.StartsWith("bluez_", StringComparison.Ordinal))
        {
            return null;
        }

        var source = direction == AudioDirection.Output ? device.Id + ".monitor" : device.Id;
        var stop = new CancellationTokenSource();
        _ = Task.Run(() => MeterLoopAsync(parec, source, peak, stop.Token));
        return new MeterHandle(stop);
    }

    /// <summary>
    /// Records the source at a low rate (8 kHz mono float) with parec and reports the peak of every
    /// 50 ms block. The stream is pinned to its device so default changes and "move apps" leave it alone.
    /// </summary>
    private static async Task MeterLoopAsync(string parec, string source, Action<float> peak, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using (var process = new Process
            {
                StartInfo = ProcessRunner.CreateStartInfo(parec,
                [
                    "--device=" + source, "--raw", "--format=float32le", "--channels=1", "--rate=8000",
                    "--latency-msec=50", "--client-name=Macro Deck Audio Switcher", "--stream-name=Level meter",
                    "--property=node.dont-move=true", "--property=node.dont-reconnect=true",
                ]),
            })
            {
                try
                {
                    process.Start();
                    using var registration = cancellationToken.Register(() => ProcessRunner.TryKill(process));
                    var stream = process.StandardOutput.BaseStream;
                    var block = new byte[400 * sizeof(float)]; // 50 ms at 8 kHz
                    var filled = 0;
                    while (true)
                    {
                        var read = await stream.ReadAsync(block.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break; // parec quit (device gone, server restarted)
                        }

                        filled += read;
                        if (filled == block.Length)
                        {
                            peak(Peak(block));
                            filled = 0;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Win32Exception)
                {
                    return;
                }
                catch (IOException)
                {
                }
                finally
                {
                    ProcessRunner.TryKill(process);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static float Peak(byte[] block)
    {
        var max = 0f;
        foreach (var sample in MemoryMarshal.Cast<byte, float>(block))
        {
            var level = Math.Abs(sample);
            if (level > max && float.IsFinite(level))
            {
                max = level;
            }
        }

        return Math.Min(max, 1f);
    }

    private sealed class MeterHandle(CancellationTokenSource stop) : IDisposable
    {
        // Not disposed: the meter loop may still be unwinding with this token.
        public void Dispose() => stop.Cancel();
    }

    private async Task RunCheckedAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Pactl, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new AudioBackendException($"pactl {string.Join(' ', arguments)} failed: {message.Trim()}");
        }
    }

    private sealed record PactlDevice(string Index, AudioDevice Device, bool IsMonitor);

    private async Task<IReadOnlyList<PactlDevice>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        if (!_jsonUnsupported)
        {
            var json = await ProcessRunner.RunAsync(Pactl, ["-f", "json", "list", kind], cancellationToken).ConfigureAwait(false);
            if (json.ExitCode == 0 && json.StandardOutput.TrimStart().StartsWith('['))
            {
                try
                {
                    return ParseJson(json.StandardOutput);
                }
                catch (JsonException)
                {
                }
            }

            // pactl older than 16 has no JSON output.
            _jsonUnsupported = true;
        }

        var text = await ProcessRunner.RunAsync(Pactl, ["list", kind], cancellationToken).ConfigureAwait(false);
        if (text.ExitCode != 0)
        {
            throw new AudioBackendException(
                $"Could not list audio {kind}: {(text.StandardError + text.StandardOutput).Trim()}");
        }

        return ParseText(text.StandardOutput);
    }

    private static IReadOnlyList<PactlDevice> ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var devices = new List<PactlDevice>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = Str(element, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = Str(element, "description");
            var index = element.TryGetProperty("index", out var indexElement) ? indexElement.ToString() : string.Empty;
            string? deviceClass = null;
            if (element.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                deviceClass = Str(properties, "device.class");
            }

            var monitorOf = Str(element, "monitor_of_sink");
            var isMonitor = name.EndsWith(".monitor", StringComparison.Ordinal)
                || string.Equals(deviceClass, "monitor", StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(monitorOf) && monitorOf != "n/a");

            devices.Add(new PactlDevice(index, new AudioDevice(name, string.IsNullOrWhiteSpace(description) ? name : description), isMonitor));
        }

        return devices;
    }

    private static string? Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<PactlDevice> ParseText(string text)
    {
        var devices = new List<PactlDevice>();
        string? index = null, name = null, description = null, deviceClass = null, monitorOf = null;

        void Flush()
        {
            if (index is not null && !string.IsNullOrEmpty(name))
            {
                var isMonitor = name.EndsWith(".monitor", StringComparison.Ordinal)
                    || deviceClass == "monitor"
                    || (!string.IsNullOrEmpty(monitorOf) && monitorOf != "n/a");
                devices.Add(new PactlDevice(index, new AudioDevice(name, string.IsNullOrWhiteSpace(description) ? name : description), isMonitor));
            }

            index = name = description = deviceClass = monitorOf = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Sink #", StringComparison.Ordinal) || line.StartsWith("Source #", StringComparison.Ordinal))
            {
                Flush();
                index = line[(line.IndexOf('#') + 1)..].Trim();
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("Name: ", StringComparison.Ordinal))
            {
                name ??= trimmed["Name: ".Length..];
            }
            else if (trimmed.StartsWith("Description: ", StringComparison.Ordinal))
            {
                description ??= trimmed["Description: ".Length..];
            }
            else if (trimmed.StartsWith("Monitor of Sink: ", StringComparison.Ordinal))
            {
                monitorOf = trimmed["Monitor of Sink: ".Length..];
            }
            else if (trimmed.StartsWith("device.class = ", StringComparison.Ordinal))
            {
                deviceClass = trimmed["device.class = ".Length..].Trim('"');
            }
        }

        Flush();
        return devices;
    }

    private async Task<(string? Sink, string? Source)> DefaultsAsync(CancellationToken cancellationToken)
    {
        var sink = await ProcessRunner.RunAsync(Pactl, ["get-default-sink"], cancellationToken).ConfigureAwait(false);
        var source = await ProcessRunner.RunAsync(Pactl, ["get-default-source"], cancellationToken).ConfigureAwait(false);
        if (sink.ExitCode == 0 && source.ExitCode == 0)
        {
            return (NullIfEmpty(sink.StandardOutput), NullIfEmpty(source.StandardOutput));
        }

        // pactl older than 15: read the defaults from "pactl info".
        var info = await ProcessRunner.RunAsync(Pactl, ["info"], cancellationToken).ConfigureAwait(false);
        string? defaultSink = null, defaultSource = null;
        foreach (var line in info.StandardOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Default Sink: ", StringComparison.Ordinal))
            {
                defaultSink = NullIfEmpty(trimmed["Default Sink: ".Length..]);
            }
            else if (trimmed.StartsWith("Default Source: ", StringComparison.Ordinal))
            {
                defaultSource = NullIfEmpty(trimmed["Default Source: ".Length..]);
            }
        }

        return (defaultSink, defaultSource);
    }

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
