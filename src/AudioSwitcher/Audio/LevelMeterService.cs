using Microsoft.Extensions.Logging;

namespace AudioSwitcher.Audio;

/// <summary>
/// Live level meters for the devices shown on widgets. Meters only run while at least one widget asks
/// for them (<see cref="Acquire"/>), follow devices as they come and go, and publish all levels together
/// about ten times a second as 0..1 values on a -60..0 dB scale with a falling peak.
/// </summary>
public sealed class LevelMeterService : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private const double FallPerTick = 0.06;  // how fast the bar drops (fraction of the scale per tick)
    private const double ChangeThreshold = 0.01;

    private readonly AudioService _audio;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Meter> _meters = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private int _outputLeases;
    private int _inputLeases;
    private bool _disposed;
    private IReadOnlyDictionary<string, double> _published = new Dictionary<string, double>();

    public LevelMeterService(AudioService audio, ILogger logger)
    {
        _audio = audio;
        _logger = logger;
        _timer = new Timer(_ => Publish(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _audio.SnapshotChanged += _ => Reconcile();
    }

    /// <summary>Raised on a background thread with every metered device's level, keyed by <see cref="Key"/>.</summary>
    public event Action<IReadOnlyDictionary<string, double>>? LevelsChanged;

    public IReadOnlyDictionary<string, double> Current => _published;

    public static string Key(AudioDirection direction, string deviceId)
        => (direction == AudioDirection.Output ? "o:" : "i:") + deviceId;

    /// <summary>Keeps meters running for the given directions until the returned lease is disposed.</summary>
    public IDisposable Acquire(bool outputs, bool inputs)
    {
        lock (_gate)
        {
            if (outputs)
            {
                _outputLeases++;
            }

            if (inputs)
            {
                _inputLeases++;
            }
        }

        Reconcile();
        return new Lease(this, outputs, inputs);
    }

    private void Release(bool outputs, bool inputs)
    {
        lock (_gate)
        {
            if (outputs)
            {
                _outputLeases--;
            }

            if (inputs)
            {
                _inputLeases--;
            }
        }

        Reconcile();
    }

    /// <summary>Starts meters for wanted devices that lack one and stops meters nobody needs any more.</summary>
    private void Reconcile()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var snapshot = _audio.Current;
            var wanted = new Dictionary<string, (AudioDirection Direction, AudioDevice Device)>(StringComparer.Ordinal);
            if (_outputLeases > 0)
            {
                foreach (var device in snapshot.Outputs)
                {
                    wanted[Key(AudioDirection.Output, device.Id)] = (AudioDirection.Output, device);
                }
            }

            if (_inputLeases > 0)
            {
                foreach (var device in snapshot.Inputs)
                {
                    wanted[Key(AudioDirection.Input, device.Id)] = (AudioDirection.Input, device);
                }
            }

            foreach (var key in _meters.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                _meters[key].Dispose();
                _meters.Remove(key);
            }

            foreach (var (key, (direction, device)) in wanted)
            {
                if (_meters.ContainsKey(key))
                {
                    continue;
                }

                var meter = new Meter();
                try
                {
                    meter.Handle = _audio.Backend.StartMeter(direction, device, meter.Report);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No level meter for {Device}.", device.Name);
                }

                _meters[key] = meter; // kept even without a handle, so it isn't retried every refresh
            }

            var running = _meters.Count > 0;
            _timer.Change(running ? Tick : Timeout.InfiniteTimeSpan, running ? Tick : Timeout.InfiniteTimeSpan);
            if (!running && _published.Count > 0)
            {
                _published = new Dictionary<string, double>();
                LevelsChanged?.Invoke(_published);
            }
        }
    }

    private void Publish()
    {
        Dictionary<string, double> levels;
        var changed = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            levels = new Dictionary<string, double>(_meters.Count, StringComparer.Ordinal);
            foreach (var (key, meter) in _meters)
            {
                if (meter.Handle is null)
                {
                    continue;
                }

                var level = meter.Advance();
                levels[key] = level;
                if (!_published.TryGetValue(key, out var previous) || Math.Abs(previous - level) >= ChangeThreshold)
                {
                    changed = true;
                }
            }

            if (!changed && levels.Count == _published.Count)
            {
                return;
            }

            _published = levels;
        }

        LevelsChanged?.Invoke(levels);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            foreach (var meter in _meters.Values)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }

    private sealed class Meter : IDisposable
    {
        private readonly object _gate = new();
        private float _peak;
        private double _display;

        public IDisposable? Handle { get; set; }

        /// <summary>Called by the backend with the peak (0..1 linear) of each short block of audio.</summary>
        public void Report(float peak)
        {
            lock (_gate)
            {
                if (peak > _peak)
                {
                    _peak = peak;
                }
            }
        }

        /// <summary>Turns the loudest peak since the last tick into a bar position, falling smoothly.</summary>
        public double Advance()
        {
            float peak;
            lock (_gate)
            {
                peak = _peak;
                _peak = 0;
            }

            var target = peak <= 0.000001f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
            _display = target >= _display ? target : Math.Max(target, _display - FallPerTick);
            return Math.Round(_display, 3);
        }

        public void Dispose() => Handle?.Dispose();
    }

    private sealed class Lease(LevelMeterService owner, bool outputs, bool inputs) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(outputs, inputs);
            }
        }
    }
}
