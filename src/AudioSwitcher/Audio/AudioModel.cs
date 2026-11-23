namespace AudioSwitcher.Audio;

public enum AudioDirection
{
    Output,
    Input,
}

/// <summary>One playback or capture device as the system reports it.</summary>
/// <param name="Id">Stable system identifier (PulseAudio/PipeWire sink or source name, or the macOS device name).</param>
/// <param name="Name">Human-readable name shown on the deck.</param>
public sealed record AudioDevice(string Id, string Name);

public sealed record AudioSnapshot(
    IReadOnlyList<AudioDevice> Outputs,
    IReadOnlyList<AudioDevice> Inputs,
    string? DefaultOutput,
    string? DefaultInput,
    string? Error)
{
    public static AudioSnapshot Empty { get; } = new([], [], null, null, null);

    public static AudioSnapshot Failed(string error) => new([], [], null, null, error);

    public IReadOnlyList<AudioDevice> Devices(AudioDirection direction)
        => direction == AudioDirection.Output ? Outputs : Inputs;

    public string? Default(AudioDirection direction)
        => direction == AudioDirection.Output ? DefaultOutput : DefaultInput;

    /// <summary>A value that changes whenever anything visible on the widget changes.</summary>
    public string Signature()
    {
        static string Join(IEnumerable<AudioDevice> devices)
            => string.Join("\u001f", devices.Select(d => d.Id + "\u001e" + d.Name));

        return string.Join("\u001d", Join(Outputs), Join(Inputs), DefaultOutput, DefaultInput, Error);
    }

    /// <summary>Finds a device by id, falling back to its display name so actions survive id changes.</summary>
    public AudioDevice? Find(AudioDirection direction, string idOrName)
    {
        var devices = Devices(direction);
        return devices.FirstOrDefault(d => string.Equals(d.Id, idOrName, StringComparison.Ordinal))
            ?? devices.FirstOrDefault(d => string.Equals(d.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A failure the user should see, with a message that explains it.</summary>
public sealed class AudioBackendException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAudioBackend
{
    /// <summary>How often to re-read the device list even without change notifications.</summary>
    TimeSpan PollInterval { get; }

    Task<AudioSnapshot> QueryAsync(CancellationToken cancellationToken);

    Task SetDefaultAsync(AudioDirection direction, string deviceId, bool moveStreams, CancellationToken cancellationToken);

    /// <summary>Calls <paramref name="changed"/> whenever the system reports a device change. Returns when cancelled.</summary>
    Task WatchAsync(Action changed, CancellationToken cancellationToken);

    /// <summary>
    /// Starts reporting the peak level (0..1, linear) of a device's audio to <paramref name="peak"/> a few
    /// times a second until the result is disposed. Returns null when this device can't be metered.
    /// </summary>
    IDisposable? StartMeter(AudioDirection direction, AudioDevice device, Action<float> peak) => null;
}
