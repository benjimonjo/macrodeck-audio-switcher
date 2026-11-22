using System.Text.Json;
using AudioSwitcher.Audio;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Localization;

namespace AudioSwitcher.Actions;

/// <summary>Sets the system default output and/or input device. Either can be left unchanged.</summary>
internal sealed class SetAudioDevicesAction(AudioService audio) : IDynamicOptionsActionDefinition
{
    public const string ActionId = "set-audio-devices";
    private const string OutputParameter = "output";
    private const string InputParameter = "input";
    private const string MoveStreamsParameter = "moveStreams";

    public string Id => ActionId;

    public LocalizedText Name => Strings.Actions.SetDevices.Name();

    public LocalizedText Description => Strings.Actions.SetDevices.Description();

    public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice(
            OutputParameter,
            Strings.Actions.SetDevices.Output.Label(),
            Strings.Actions.SetDevices.Output.Description()).AsSearchable(),
        ActionParameter.DynamicChoice(
            InputParameter,
            Strings.Actions.SetDevices.Input.Label(),
            Strings.Actions.SetDevices.Input.Description()).AsSearchable(),
        ActionParameter.Toggle(
            MoveStreamsParameter,
            Strings.Actions.SetDevices.MoveStreams.Label(),
            Strings.Actions.SetDevices.MoveStreams.Description(),
            defaultValue: true),
    ];

    public IActionExecutor CreateExecutor() => new Executor(audio);

    public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
    {
        var direction = context.ParameterName == InputParameter ? AudioDirection.Input : AudioDirection.Output;
        var snapshot = await audio.RefreshAsync(cancellationToken).ConfigureAwait(false);

        var options = new List<ActionParameterOption>
        {
            new() { Value = string.Empty, Label = Strings.Actions.SetDevices.Unchanged() },
        };
        options.AddRange(snapshot.Devices(direction).Select(device => new ActionParameterOption
        {
            Value = device.Id,
            Label = device.Name,
        }));

        return snapshot.Error is { } error
            ? new DynamicOptionsResult { Options = options, CacheSeconds = 5, Error = error }
            : new DynamicOptionsResult { Options = options, CacheSeconds = 5 };
    }

    private sealed class Executor(AudioService audio) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var output = ReadString(context.Parameters, OutputParameter);
            var input = ReadString(context.Parameters, InputParameter);
            var moveStreams = ReadBool(context.Parameters, MoveStreamsParameter, fallback: true);

            if (output is null && input is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Actions.SetDevices.NothingSelected());
            }

            try
            {
                if (output is not null)
                {
                    await audio.SetDefaultAsync(AudioDirection.Output, output, moveStreams, context.CancellationToken)
                        .ConfigureAwait(false);
                }

                if (input is not null)
                {
                    await audio.SetDefaultAsync(AudioDirection.Input, input, moveStreams, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (AudioBackendException ex)
            {
                return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.SetDevices.Failed(ex.Message));
            }

            return ActionResult.Success();
        }

        private static string? ReadString(IReadOnlyDictionary<string, object> parameters, string name)
        {
            if (!parameters.TryGetValue(name, out var value) || value is null)
            {
                return null;
            }

            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                JsonElement element => element.ToString(),
                _ => value.ToString(),
            };
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static bool ReadBool(IReadOnlyDictionary<string, object> parameters, string name, bool fallback)
        {
            if (!parameters.TryGetValue(name, out var value) || value is null)
            {
                return fallback;
            }

            return value switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                JsonElement { ValueKind: JsonValueKind.String } element => bool.TryParse(element.GetString(), out var parsed) ? parsed : fallback,
                string s => bool.TryParse(s, out var parsed) ? parsed : fallback,
                _ => fallback,
            };
        }
    }
}
