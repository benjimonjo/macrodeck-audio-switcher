using System.Text.Json;
using AudioSwitcher.Audio;
using MacroDeck.Localization;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace AudioSwitcher.Widget;

/// <summary>
/// The widget's settings screen. Macro Deck draws the preview, saves, and keeps unsaved-change
/// prompts; each top-level input's <c>Key</c> is the widget-data key it writes.
/// </summary>
internal static class AudioDevicesConfigView
{
    public static UiElement Build(JsonElement data, AudioSnapshot snapshot)
    {
        data = WidgetOptions.Normalize(data);
        var options = WidgetOptions.Parse(data);
        var appearanceData = data.ValueKind == JsonValueKind.Object ? data : JsonDocument.Parse("{}").RootElement.Clone();

        return new UiWidgetConfiguration
        {
            Key = "root",
            Properties = new UiWidgetProperties
            {
                Key = "properties",
                Children =
                [
                    new UiHeading { Key = "layout-heading", Text = Strings.Widgets.AudioDevices.Config.LayoutHeading() },
                    new UiChoiceInput
                    {
                        Key = WidgetOptions.OverflowKey,
                        Label = Strings.Widgets.AudioDevices.Config.Overflow.Label(),
                        Description = Strings.Widgets.AudioDevices.Config.Overflow.Description(),
                        Segmented = true,
                        Options = Choices(
                            (WidgetOptions.OverflowFit, Strings.Widgets.AudioDevices.Config.Overflow.Fit()),
                            (WidgetOptions.OverflowScroll, Strings.Widgets.AudioDevices.Config.Overflow.Scroll())),
                        DefaultValue = WidgetOptions.OverflowFit,
                        Binding = Bind.To(new UiState<string>(options.Overflow)),
                    },
                    new UiChoiceInput
                    {
                        Key = WidgetOptions.ColumnsKey,
                        Label = Strings.Widgets.AudioDevices.Config.Columns.Label(),
                        Segmented = true,
                        Options = Choices(
                            (WidgetOptions.ColumnsBoth, Strings.Widgets.AudioDevices.Config.Columns.Both()),
                            (WidgetOptions.ColumnsOutput, Strings.Widgets.AudioDevices.Config.Columns.OutputOnly()),
                            (WidgetOptions.ColumnsInput, Strings.Widgets.AudioDevices.Config.Columns.InputOnly())),
                        DefaultValue = WidgetOptions.ColumnsBoth,
                        Binding = Bind.To(new UiState<string>(options.Columns)),
                    },
                    new UiChoiceInput
                    {
                        Key = WidgetOptions.MetersKey,
                        Label = Strings.Widgets.AudioDevices.Config.Meters.Label(),
                        Description = Strings.Widgets.AudioDevices.Config.Meters.Description(),
                        Segmented = true,
                        Options = Choices(
                            (WidgetOptions.MetersOff, Strings.Widgets.AudioDevices.Config.Meters.Off()),
                            (WidgetOptions.MetersOutputs, Strings.Widgets.AudioDevices.Config.Meters.Outputs()),
                            (WidgetOptions.MetersInputs, Strings.Widgets.AudioDevices.Config.Meters.Inputs()),
                            (WidgetOptions.MetersBoth, Strings.Widgets.AudioDevices.Config.Meters.Both())),
                        DefaultValue = WidgetOptions.MetersOff,
                        Binding = Bind.To(new UiState<string>(options.Meters)),
                    },
                    new UiBooleanInput
                    {
                        Key = WidgetOptions.MoveStreamsKey,
                        Label = Strings.Widgets.AudioDevices.Config.MoveStreams.Label(),
                        Description = Strings.Widgets.AudioDevices.Config.MoveStreams.Description(),
                        DefaultValue = true,
                        Binding = Bind.To(new UiState<bool>(options.MoveStreams)),
                    },
                    new UiHeading { Key = "devices-heading", Text = Strings.Widgets.AudioDevices.Config.DevicesHeading() },
                    new UiMultiSelectInput
                    {
                        Key = WidgetOptions.HiddenOutputsKey,
                        Label = Strings.Widgets.AudioDevices.Config.HiddenOutputs.Label(),
                        Description = Strings.Widgets.AudioDevices.Config.HiddenOutputs.Description(),
                        Options = DeviceChoices(snapshot.Outputs, options.HiddenOutputs),
                        Binding = Bind.To(new UiState<IReadOnlyList<string>>(options.HiddenOutputs)),
                    },
                    new UiMultiSelectInput
                    {
                        Key = WidgetOptions.HiddenInputsKey,
                        Label = Strings.Widgets.AudioDevices.Config.HiddenInputs.Label(),
                        Description = Strings.Widgets.AudioDevices.Config.HiddenInputs.Description(),
                        Options = DeviceChoices(snapshot.Inputs, options.HiddenInputs),
                        Binding = Bind.To(new UiState<IReadOnlyList<string>>(options.HiddenInputs)),
                    },
                    // Macro Deck's own background / accent colour / border fields, the same as other widgets.
                    UiWidgetAppearance.Section(
                        appearanceData,
                        UiWidgetAppearanceFields.BackgroundColor | UiWidgetAppearanceFields.AccentColor | UiWidgetAppearanceFields.Border),
                ],
            },
        };
    }

    private static UiValue<IReadOnlyList<UiOption>> Choices(params (string Value, LocalizedString Label)[] choices)
        => UiValue.Of<IReadOnlyList<UiOption>>(choices.Select(c => UiOption.Of(c.Value, c.Label)).ToList());

    /// <summary>Every connected device, plus any hidden one that is unplugged right now so it stays hidden.</summary>
    private static UiValue<IReadOnlyList<UiOption>> DeviceChoices(IReadOnlyList<AudioDevice> devices, IReadOnlyList<string> hidden)
    {
        var options = devices.Select(d => UiOption.Of(d.Id, d.Name)).ToList();
        options.AddRange(hidden
            .Where(id => devices.All(d => d.Id != id))
            .Select(id => UiOption.Of(id, id)));
        return UiValue.Of<IReadOnlyList<UiOption>>(options);
    }
}

/// <summary>Adapts a <see cref="UiView"/> to Macro Deck's <see cref="IUiSession"/>.</summary>
internal sealed class ViewSession : IUiSession
{
    private readonly UiView _view;

    public ViewSession(UiView view)
    {
        _view = view;
        _view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _view.HandlerFaulted += (_, fault)
            => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));
    }

    public event EventHandler? Changed;

    public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

    public UiTree BuildTree() => _view.Tree;

    public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

    public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

    public void Dispatch(UiEvent uiEvent, IUiEventOrigin origin) => _view.Dispatch(uiEvent);

    public ValueTask DisposeAsync()
    {
        _view.Dispose();
        return ValueTask.CompletedTask;
    }
}
