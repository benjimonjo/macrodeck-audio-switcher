using System.Text.Json;
using MacroDeck.Ui.Config;

namespace AudioSwitcher.Widget;

/// <summary>The settings stored in an "Audio devices" widget's data, edited on its settings screen.</summary>
internal sealed record WidgetOptions(
    string Overflow,
    string Columns,
    bool MoveStreams,
    IReadOnlyList<string> HiddenOutputs,
    IReadOnlyList<string> HiddenInputs,
    string Meters,
    string? BackgroundColor,
    string? AccentColor)
{
    public const string OverflowKey = "overflow";
    public const string ColumnsKey = "columns";
    public const string MoveStreamsKey = "moveStreams";
    public const string HiddenOutputsKey = "hiddenOutputs";
    public const string HiddenInputsKey = "hiddenInputs";
    public const string MetersKey = "meters";

    public const string OverflowFit = "fit";
    public const string OverflowScroll = "scroll";
    public const string ColumnsBoth = "both";
    public const string ColumnsOutput = "output";
    public const string ColumnsInput = "input";
    public const string MetersOff = "off";
    public const string MetersOutputs = "outputs";
    public const string MetersInputs = "inputs";
    public const string MetersBoth = "both";

    public static WidgetOptions Default { get; } = new(OverflowFit, ColumnsBoth, true, [], [], MetersOff, null, null);

    public const string DefaultDataJson = """{"overflow":"fit","columns":"both","moveStreams":true,"hiddenOutputs":[],"hiddenInputs":[],"meters":"off"}""";

    public const string DataSchemaJson = """
        {
          "type": "object",
          "properties": {
            "overflow": { "type": "string", "enum": ["fit", "scroll"] },
            "columns": { "type": "string", "enum": ["both", "output", "input"] },
            "moveStreams": { "type": "boolean" },
            "hiddenOutputs": { "type": "array", "items": { "type": "string" } },
            "hiddenInputs": { "type": "array", "items": { "type": "string" } },
            "meters": { "type": "string", "enum": ["off", "outputs", "inputs", "both"] }
          }
        }
        """;

    public bool Scroll => Overflow == OverflowScroll;

    public bool ShowOutputs => Columns != ColumnsInput;

    public bool ShowInputs => Columns != ColumnsOutput;

    /// <summary>Whether a column shows level meters: the setting allows it and the column is visible.</summary>
    public bool ShowMeters(AudioSwitcher.Audio.AudioDirection direction) => direction == AudioSwitcher.Audio.AudioDirection.Output
        ? ShowOutputs && Meters is MetersOutputs or MetersBoth
        : ShowInputs && Meters is MetersInputs or MetersBoth;

    /// <summary>Accepts widget data as a JSON object or as a JSON string holding one.</summary>
    public static JsonElement Normalize(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(data.GetString() ?? "{}");
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        return data;
    }

    public static WidgetOptions Parse(JsonElement data)
    {
        data = Normalize(data);
        if (data.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        var appearance = UiWidgetAppearance.Read(data);
        return new WidgetOptions(
            Str(data, OverflowKey) is OverflowScroll ? OverflowScroll : OverflowFit,
            Str(data, ColumnsKey) is ColumnsOutput or ColumnsInput ? Str(data, ColumnsKey)! : ColumnsBoth,
            !data.TryGetProperty(MoveStreamsKey, out var move) || move.ValueKind != JsonValueKind.False,
            StringList(data, HiddenOutputsKey),
            StringList(data, HiddenInputsKey),
            Str(data, MetersKey) is MetersOutputs or MetersInputs or MetersBoth ? Str(data, MetersKey)! : MetersOff,
            NullIfEmpty(appearance.BackgroundColor),
            NullIfEmpty(appearance.AccentColor));
    }

    private static string? Str(JsonElement data, string key)
        => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> StringList(JsonElement data, string key)
        => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList()
            : [];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
