using System.Text.Json.Nodes;

namespace AsterLauncher.Infrastructure;

/// <summary>Removes credentials and record links before archives or exports are written.</summary>
internal static class GachaArchivePrivacy
{
    internal static void Strip(JsonNode? node, Uri? source = null)
    {
        var secrets = new List<string>();
        if (source is not null && GachaHistoryUrlValidator.TryParseQuery(source.Query, out var query))
        {
            foreach (var pair in query)
                if (IsSecretKey(pair.Key) && pair.Value.Length >= 16)
                {
                    secrets.Add(pair.Value);
                    secrets.Add(Uri.EscapeDataString(pair.Value));
                }
        }
        StripNode(node, secrets);
    }

    private static bool IsSecretKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("authkey", StringComparison.Ordinal)
            || normalized is "authorization" or "signature";
    }

    private static bool ContainsSecret(JsonNode? node, IReadOnlyList<string> secrets) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
        && (text.Contains("authkey=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authkey%3d", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token%3d", StringComparison.OrdinalIgnoreCase)
            || secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)));

    private static void StripNode(JsonNode? node, IReadOnlyList<string> secrets)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
                if (IsSecretKey(key) || ContainsSecret(obj[key], secrets)) obj.Remove(key);
                else StripNode(obj[key], secrets);
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (ContainsSecret(array[i], secrets)) array[i] = null;
                else StripNode(array[i], secrets);
        }
    }
}

