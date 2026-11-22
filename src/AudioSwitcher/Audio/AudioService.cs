using Microsoft.Extensions.Logging;

namespace AudioSwitcher.Audio;

/// <summary>
/// Keeps an up-to-date picture of the system's audio devices and switches the defaults.
/// One instance is shared by every widget and action of the plugin.
/// </summary>
public sealed class AudioService : IAsyncDisposable
{
    private readonly IAudioBackend _backend;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private int _refreshQueued;
    private Task? _watchLoop;
    private Task? _pollLoop;
    private string _signature = AudioSnapshot.Empty.Signature();

    public AudioService(ILogger logger)
    {
        _logger = logger;
        _backend = OperatingSystem.IsLinux() ? (IAudioBackend)new PactlBackend()
            : OperatingSystem.IsMacOS() ? new SwitchAudioSourceBackend()
            : OperatingSystem.IsWindows() ? new WindowsCoreAudioBackend()
            : new UnsupportedBackend("Switching audio devices is not supported on this operating system.");
    }

    public AudioSnapshot Current { get; private set; } = AudioSnapshot.Empty;

    public bool HasLoaded { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="Current"/> changes.</summary>
    public event Action<AudioSnapshot>? SnapshotChanged;

    public void Start()
    {
        if (_watchLoop is not null)
        {
            return;
        }

        var token = _stopping.Token;
        _watchLoop = Task.Run(() => WatchLoopAsync(token), token);
        _pollLoop = Task.Run(() => PollLoopAsync(token), token);
    }

    public async Task<AudioSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AudioSnapshot snapshot;
            try
            {
                snapshot = await _backend.QueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AudioBackendException ex)
            {
                snapshot = AudioSnapshot.Failed(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reading the audio devices failed.");
                snapshot = AudioSnapshot.Failed("Could not read the audio devices: " + ex.Message);
            }

            HasLoaded = true;
            var signature = snapshot.Signature();
            if (signature != _signature)
            {
                _signature = signature;
                Current = snapshot;
                SnapshotChanged?.Invoke(snapshot);
            }

            return Current;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Sets the system default device. Throws <see cref="AudioBackendException"/> with a user-facing message.</summary>
    public async Task<AudioDevice> SetDefaultAsync(
        AudioDirection direction,
        string idOrName,
        bool moveStreams,
        CancellationToken cancellationToken)
    {
        var snapshot = HasLoaded ? Current : await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var device = snapshot.Find(direction, idOrName);
        if (device is null)
        {
            // The list may be stale (device just plugged in): read it again before giving up.
            snapshot = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            device = snapshot.Find(direction, idOrName)
                ?? throw new AudioBackendException(
                    $"The {(direction == AudioDirection.Output ? "output" : "input")} device '{idOrName}' is not connected.");
        }

        await _backend.SetDefaultAsync(direction, device.Id, moveStreams, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Default {Direction} set to {Device} ({Id}).", direction, device.Name, device.Id);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return device;
    }

    private void QueueRefresh()
    {
        // Coalesce bursts of change events (PipeWire sends several per switch) into one re-read.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), token).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refreshing the audio devices failed.");
            }
        }, token);
    }

    private async Task WatchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _backend.WatchAsync(QueueRefresh, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio change feed unavailable; relying on polling.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling the audio devices failed.");
            }

            try
            {
                await Task.Delay(_backend.PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var loop in new[] { _watchLoop, _pollLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }
}
