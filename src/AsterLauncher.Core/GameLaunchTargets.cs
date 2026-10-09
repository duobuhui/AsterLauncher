using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsterLauncher.Core;

public enum GameLaunchTarget
{
    Local = 0,
    OfficialCloud = 1,
    Emulator = 2
}

public static class GameLaunchTargets
{
    public static bool SupportsOfficialCloud(string? gameId) => gameId is
        BuiltInGameIds.Endfield or BuiltInGameIds.GenshinImpact or
        BuiltInGameIds.HonkaiStarRail or BuiltInGameIds.ZenlessZoneZero;

    public static bool SupportsEmulator(string? gameId) => gameId == BuiltInGameIds.Arknights;

    public static GameLaunchTarget Normalize(string? gameId, GameLaunchTarget target) => target switch
    {
        GameLaunchTarget.OfficialCloud when SupportsOfficialCloud(gameId) => GameLaunchTarget.OfficialCloud,
        GameLaunchTarget.Emulator when SupportsEmulator(gameId) => GameLaunchTarget.Emulator,
        _ => GameLaunchTarget.Local
    };
}

// A future launch mode must not make the rest of an existing configuration unreadable.
// This property-level converter also takes precedence over the store's general enum converter.
public sealed class GameLaunchTargetJsonConverter : JsonConverter<GameLaunchTarget>
{
    public override GameLaunchTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, nameof(GameLaunchTarget.OfficialCloud), StringComparison.OrdinalIgnoreCase))
                return GameLaunchTarget.OfficialCloud;
            if (string.Equals(value, nameof(GameLaunchTarget.Emulator), StringComparison.OrdinalIgnoreCase))
                return GameLaunchTarget.Emulator;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue)
            && Enum.IsDefined((GameLaunchTarget)numericValue))
        {
            return (GameLaunchTarget)numericValue;
        }

        // Scalar values have already been consumed. Skip() rejects every
        // non-final buffer, even for "Local", so it must not be used here.
        // The serializer buffers custom converter values; ParseValue also
        // consumes an unknown object/array without requiring the final buffer.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var ignored = JsonDocument.ParseValue(ref reader);
        }
        return GameLaunchTarget.Local;
    }

    public override void Write(Utf8JsonWriter writer, GameLaunchTarget value, JsonSerializerOptions options)
        => writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : nameof(GameLaunchTarget.Local));
}
