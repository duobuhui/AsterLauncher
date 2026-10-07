namespace AsterLauncher.Core;

/// <summary>Public display data only; no executable payloads, game files or account information.</summary>
public sealed record ResourceCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public IReadOnlyList<ResourcePool> Pools { get; init; } = [];
    public IReadOnlyList<ResourceImage> Images { get; init; } = [];
    public IReadOnlyList<ResourceAnnouncement> Announcements { get; init; } = [];
    public IReadOnlyList<ResourceCode> Codes { get; init; } = [];
}
public sealed record ResourcePool
{
    public string GameId { get; init; } = "";
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Phase { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public DateOnly? StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public string? Category { get; init; }
    public string? FeaturedOperator { get; init; }
    public string? WeaponPool { get; init; }
    public string? Weapon { get; init; }
    public string Source { get; init; } = "";
    // Date-only boundaries are not fabricated midnight timestamps. Use a consistent date range if either boundary has day precision.
    public string DateText => StartsOn is not null || EndsOn is not null
        ? $"{FormatDate(StartsOn, StartsAt)} — {FormatDate(EndsOn, EndsAt)}"
        : (StartsAt, EndsAt) switch
    {
        ({ } start, { } end) => $"{Format(start)} — {Format(end)}",
        ({ } start, null) => $"{Format(start)} — 未公布",
        _ => ""
    };
    private static string FormatDate(DateOnly? date, DateTimeOffset? time) =>
        (date ?? (time is { } value ? DateOnly.FromDateTime(value.ToOffset(TimeSpan.FromHours(8)).DateTime) : null))
        ?.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) ?? "未公布";
    private static string Format(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm");
}
public sealed record ResourceImage
{
    public string GameId { get; init; } = "";
    public string Kind { get; init; } = ""; // portrait or banner
    public string Key { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string File { get; init; } = ""; // relative to the public repository's resources directory
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public string Source { get; init; } = "";
    public string Attribution { get; init; } = "";
}
public sealed record ResourceAnnouncement
{
    public string GameId { get; init; } = "";
    public string Kind { get; init; } = ""; // event, maintenance, livestream
    public string Title { get; init; } = "";
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public string Url { get; init; } = "";
    public string Region { get; init; } = "CN";
}
public sealed record ResourceCode
{
    public string GameId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Reward { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Region { get; init; } = "CN";
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public string Source { get; init; } = "";
}
public interface IResourceCatalogProvider
{
    ResourceCatalog? Current { get; }
    string? FindImage(string gameId, string kind, string key);
    event EventHandler? Updated;
}
