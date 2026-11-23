using System.Text.Json;
using AudioSwitcher.Actions;
using AudioSwitcher.Audio;
using AudioSwitcher.Widget;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using Microsoft.Extensions.Logging;

namespace AudioSwitcher;

/// <summary>
/// Audio Device Switcher: an "Audio devices" deck widget (best at 4x3) that lists the system's outputs and
/// inputs live and switches the default on tap, and a "Set audio devices" action for buttons and flows.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IWidgetTypeProvider, IUiProvider
{
    private readonly ILogger<PluginIntegration> _logger;
    private readonly AudioService _audio;
    private readonly LevelMeterService _meters;
    private string? _widgetTypeId;

    public PluginIntegration(ILogger<PluginIntegration> logger)
    {
        _logger = logger;
        _audio = new AudioService(logger);
        _meters = new LevelMeterService(_audio, logger);
        Actions = [new SetAudioDevicesAction(_audio)];
    }

    public IReadOnlyList<IActionDefinition> Actions { get; }

    public Task InitializeAsync(IIntegrationContext context)
    {
        _audio.Start();
        _logger.LogInformation("Audio Device Switcher initialized.");
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        _meters.Dispose();
        await _audio.DisposeAsync().ConfigureAwait(false);
    }

    // ----- Widget type -----------------------------------------------------------------------------

    public string ProviderName => "Audio Device Switcher";

    public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() =>
    [
        new WidgetTypeDescriptor(
            AudioDevicesWidget.LocalId,
            Strings.Widgets.AudioDevices.Name(),
            (LocalizedText)Strings.Widgets.AudioDevices.Description(),
            DefaultData: WidgetOptions.DefaultDataJson,
            DataSchema: WidgetOptions.DataSchemaJson,
            HasConfiguration: true)
        {
            // Lets Macro Deck's appearance actions (Set Background Color, ...) target this widget too.
            AppearanceProperties = [WidgetAppearanceProperty.BackgroundColor, WidgetAppearanceProperty.AccentColor],
        },
    ];

    public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
    {
        foreach (var widgetType in GetWidgetTypes())
        {
            var registration = await context.RegisterWidgetTypeAsync(widgetType, cancellationToken).ConfigureAwait(false);
            _widgetTypeId = registration.WidgetTypeId;
            _logger.LogInformation("Registered widget type {WidgetType}.", registration.WidgetTypeId);
        }
    }

    // ----- UI sessions -----------------------------------------------------------------------------

    public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
    [
        new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
        new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
        new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
    ];

    public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
    {
        var surface = request.Surface;
        if (surface.Kind is UiSurfaceKinds.Widget or UiSurfaceKinds.Preview
            && IsOurWidget(Attribute(surface, UiWidgetSurfaceAttributes.WidgetType)))
        {
            var options = WidgetOptions.Parse(Attribute(surface, UiWidgetSurfaceAttributes.Data));
            return Task.FromResult<IUiSession?>(new AudioDevicesWidget(surface, options, _audio, _meters, _logger));
        }

        if (surface.Kind == UiSurfaceKinds.Config
            && Attribute(surface, UiConfigSurfaceAttributes.EntryPoint) is { ValueKind: JsonValueKind.String } entryPoint
            && entryPoint.GetString() == UiConfigEntryPoints.WidgetConfig
            && IsOurWidget(Attribute(surface, UiConfigSurfaceAttributes.WidgetType)))
        {
            if (!_audio.HasLoaded)
            {
                _ = _audio.RefreshAsync(CancellationToken.None);
            }

            var root = AudioDevicesConfigView.Build(Attribute(surface, UiConfigSurfaceAttributes.WidgetData), _audio.Current);
            return Task.FromResult<IUiSession?>(new ViewSession(new UiView(surface, root)));
        }

        return Task.FromResult<IUiSession?>(null);
    }

    private static JsonElement Attribute(UiSurface surface, string name)
        => surface.Attributes is not null && surface.Attributes.TryGetValue(name, out var value) ? value : default;

    private bool IsOurWidget(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = value.GetString();
        return type is not null
            && (string.Equals(type, _widgetTypeId, StringComparison.Ordinal)
                || string.Equals(type, AudioDevicesWidget.LocalId, StringComparison.Ordinal)
                || type.EndsWith("::" + AudioDevicesWidget.LocalId, StringComparison.Ordinal));
    }
}
