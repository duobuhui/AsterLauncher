using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsterLauncher.Core;

public enum EndfieldLaunchMode
{
    OfficialLauncher = 0,
    ExperimentalBridge = 1
}

// Unknown future modes must keep the rest of an existing configuration readable.
public sealed class EndfieldLaunchModeJsonConverter : JsonConverter<EndfieldLaunchMode>
{
    public override EndfieldLaunchMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (string.Equals(reader.GetString(), nameof(EndfieldLaunchMode.ExperimentalBridge), StringComparison.OrdinalIgnoreCase))
                return EndfieldLaunchMode.ExperimentalBridge;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
            && value == (int)EndfieldLaunchMode.ExperimentalBridge)
        {
            return EndfieldLaunchMode.ExperimentalBridge;
        }

        // ParseValue consumes an unknown object/array without relying on a final reader buffer.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var ignored = JsonDocument.ParseValue(ref reader);
        }
        return EndfieldLaunchMode.OfficialLauncher;
    }

    public override void Write(Utf8JsonWriter writer, EndfieldLaunchMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == EndfieldLaunchMode.ExperimentalBridge
            ? nameof(EndfieldLaunchMode.ExperimentalBridge) : nameof(EndfieldLaunchMode.OfficialLauncher));
}