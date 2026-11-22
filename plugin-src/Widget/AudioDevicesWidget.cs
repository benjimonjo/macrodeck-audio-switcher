using System.Security.Cryptography;
using System.Text;
using AudioSwitcher.Audio;
using MacroDeck.Localization;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Microsoft.Extensions.Logging;

namespace AudioSwitcher.Widget;

/// <summary>
/// The "Audio devices" deck widget: an Output column and an Input column listing every device the
/// system reports, live. Tapping a device makes it the system default. Laid out for a 4x3 tile.
/// Settings (see <see cref="AudioDevicesConfigView"/>) choose fit-or-scroll, which columns show,
/// hidden devices and colours.
/// </summary>
internal sealed class AudioDevicesWidget : IUiSession
{
    public const string LocalId = "audio-devices";

    private const string DefaultTileBackground = "#18181b";
    private const string RowBackground = "#27272a";
    private const string DefaultAccent = "#2563eb";
    private const string TextColor = "#e4e4e7";
    private const string SelectedTextColor = "#ffffff";
    private const string HeadingColor = "#a1a1aa";

    private readonly AudioService _audio;
    private readonly ILogger _logger;
    private readonly WidgetOptions _options;
    private readonly UiState<AudioSnapshot> _state;
    private readonly UiView _view;
    private IUiEventOrigin? _origin;

    public AudioDevicesWidget(UiSurface surface, WidgetOptions options, AudioService audio, ILogger logger)
    {
        _audio = audio;
        _logger = logger;
        _options = options;
        _state = new UiState<AudioSnapshot>(audio.Current);
        _view = new UiView(surface, Build(surface));
        _view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _view.HandlerFaulted += (_, fault)
            => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));

        _audio.SnapshotChanged += OnSnapshotChanged;
        if (!_audio.HasLoaded)
        {
            _ = _audio.RefreshAsync();
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

    public UiTree BuildTree() => _view.Tree;

    public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

    public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

    public void Dispatch(UiEvent uiEvent, IUiEventOrigin origin)
    {
        // Dispatch is never concurrent for one session, so the handler that runs now sees this origin.
        _origin = origin;
        _view.Dispatch(uiEvent);
    }

    public ValueTask DisposeAsync()
    {
        _audio.SnapshotChanged -= OnSnapshotChanged;
        _view.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnSnapshotChanged(AudioSnapshot snapshot) => _state.Set(snapshot);

    private string Accent => _options.AccentColor ?? DefaultAccent;

    /// <summary>The devices a column shows: what the system reports, minus the ones hidden in settings.</summary>
    private IReadOnlyList<AudioDevice> Visible(AudioDirection direction)
    {
        var hidden = direction == AudioDirection.Output ? _options.HiddenOutputs : _options.HiddenInputs;
        var devices = _state.Value.Devices(direction);
        return hidden.Count == 0 ? devices : devices.Where(d => !hidden.Contains(d.Id)).ToList();
    }

    private UiElement Build(UiSurface surface)
    {
        var columns = new List<UiElement>();
        if (_options.ShowOutputs)
        {
            columns.Add(Column(AudioDirection.Output, Strings.Widgets.AudioDevices.OutputHeading()));
        }

        if (_options.ShowInputs)
        {
            columns.Add(Column(AudioDirection.Input, Strings.Widgets.AudioDevices.InputHeading()));
        }

        return new UiStack
        {
            Key = "root",
            Direction = UiComponentDirections.Horizontal,
            Align = UiComponentAlignments.Stretch,
            Background = _options.BackgroundColor ?? DefaultTileBackground,
            Padding = UiWidgetSafeArea.For(surface),
            Gap = UiSize.FromBasis(0.04),
            Children = columns,
        };
    }

    private UiElement Column(AudioDirection direction, LocalizedString heading)
    {
        var key = direction == AudioDirection.Output ? "output" : "input";
        UiElement rows = new UiRepeat<AudioDevice>
        {
            Key = key + "-devices",
            Items = UiValue.From(() => Visible(direction)),
            // Device ids contain dots and other characters node ids can't carry (e.g. AirPlay
            // "raop_sink.Lounge-Room.local.192.168.1.11.7000"), so key rows by a short hash.
            KeySelector = device => SafeKey(device.Id),
            Template = (device, _) => Row(direction, device),
        };

        if (_options.Scroll)
        {
            // Fixed-size rows in a list that scrolls when they don't all fit.
            rows = new UiList
            {
                Key = key + "-list",
                Direction = UiComponentDirections.Vertical,
                Fill = true,
                Gap = UiSize.FromBasis(0.02),
                Children = [rows],
            };
        }

        return new UiStack
        {
            Key = key,
            Direction = UiComponentDirections.Vertical,
            Justify = UiComponentJustify.Start,
            Align = UiComponentAlignments.Stretch,
            Fill = true,
            Gap = Scaled(m => m.Gap),
            Children =
            [
                new UiTextRun
                {
                    Key = key + "-heading",
                    Text = heading,
                    Size = UiSize.FromBasis(0.05),
                    MainSize = UiSize.FromBasis(0.08),
                    Weight = UiComponentTextWeights.Bold,
                    Color = HeadingColor,
                    MaxLines = 1,
                },
                rows,
                new UiWhen
                {
                    Key = key + "-empty",
                    Condition = () => Visible(direction).Count == 0,
                    Content = () => new UiTextRun
                    {
                        Key = key + "-empty-text",
                        Text = UiText.Optional(() => _state.Value.Error is { } error
                            ? UiText.Of(error)
                            : UiText.Of(_audio.HasLoaded
                                ? Strings.Widgets.AudioDevices.Empty()
                                : Strings.Widgets.AudioDevices.Loading())),
                        Size = UiSize.FromBasis(0.04),
                        Role = UiComponentTextRoles.Muted,
                        Wrap = true,
                        MaxLines = 6,
                        Fill = true,
                    },
                },
            ],
        };
    }

    private static string SafeKey(string id)
        => "d" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];

    private UiElement Row(AudioDirection direction, AudioDevice device)
    {
        // Node ids must be unique across the whole tree, so every key in the row carries the device hash.
        var k = SafeKey(device.Id) + "-" + (direction == AudioDirection.Output ? "o" : "i");
        bool IsSelected() => string.Equals(_state.Value.Default(direction), device.Id, StringComparison.Ordinal);

        return new UiButton
        {
            Key = "row-" + k,
            Direction = UiComponentDirections.Horizontal,
            Justify = UiComponentJustify.Start,
            Align = UiComponentAlignments.Center,
            Corner = UiComponentButtonCorners.Tile,
            MainSize = Scaled(m => m.Row),
            Padding = Scaled(m => m.Padding),
            Gap = Scaled(m => m.Padding),
            Background = UiValue.From(() => IsSelected() ? Accent : RowBackground),
            Events = [UiEventHandler.OnAsync(UiComponentEvents.Press, ct => SelectAsync(direction, device, ct))],
            Children =
            [
                new UiIcon
                {
                    Key = "check-" + k,
                    Icon = UiIcons.Check,
                    Size = Scaled(m => m.Text),
                    MainSize = Scaled(m => m.Text * 1.1),
                    // Painted in the row colour when not selected, so the name never shifts sideways.
                    Color = UiValue.From(() => IsSelected() ? SelectedTextColor : RowBackground),
                },
                new UiTextRun
                {
                    Key = "name-" + k,
                    Text = device.Name,
                    Size = Scaled(m => m.Text),
                    MinSize = Scaled(m => m.Text * 0.8),
                    Color = UiValue.From(() => IsSelected() ? SelectedTextColor : TextColor),
                    Weight = UiValue.From(() => IsSelected() ? UiComponentTextWeights.SemiBold : UiComponentTextWeights.Regular),
                    MaxLines = UiValue.From(() => Metrics().Row < 0.11 ? 1 : 2),
                    Wrap = true,
                    Fill = true,
                },
            ],
        };
    }

    /// <summary>Row geometry, as fractions of the tile's shorter side.</summary>
    private readonly record struct RowMetrics(double Row, double Gap, double Text, double Padding);

    private static readonly RowMetrics FullSize = new(0.13, 0.02, 0.045, 0.025);

    private RowMetrics Metrics()
    {
        if (_options.Scroll)
        {
            return FullSize; // rows keep their size; the list scrolls instead
        }

        // Fit mode: shrink rows so every visible device fits. Both columns use the longer list so rows line up.
        var count = Math.Max(1, Math.Max(
            _options.ShowOutputs ? Visible(AudioDirection.Output).Count : 0,
            _options.ShowInputs ? Visible(AudioDirection.Input).Count : 0));
        const double available = 0.80; // column height left after the heading, with margin
        var gap = count > 6 ? 0.012 : FullSize.Gap;
        var row = Math.Min(FullSize.Row, (available - gap * (count - 1)) / count);
        return new RowMetrics(row, gap, Math.Min(FullSize.Text, row * 0.5), Math.Min(FullSize.Padding, row * 0.15));
    }

    private UiSize Scaled(Func<RowMetrics, double> pick)
        => UiSize.From(() => UiLength.OfBasis(pick(Metrics())));

    private async Task SelectAsync(AudioDirection direction, AudioDevice device, CancellationToken cancellationToken)
    {
        var origin = _origin; // capture before the first await; the field follows the latest event
        if (string.Equals(_state.Value.Default(direction), device.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _audio.SetDefaultAsync(direction, device.Id, _options.MoveStreams, cancellationToken).ConfigureAwait(false);
        }
        catch (AudioBackendException ex)
        {
            _logger.LogWarning("Switching {Direction} to {Device} failed: {Message}", direction, device.Name, ex.Message);
            if (origin is not null)
            {
                await origin.ReportFailureAsync(Strings.Widgets.AudioDevices.SwitchFailed(ex.Message), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
