using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class EndfieldSignatureWeaponTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));
    public EndfieldSignatureWeaponTests() { Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root); }
    [Fact]
    public async Task CardMatchesExactWeaponPoolAndExcludesGiftAndOtherPools()
    {
        var archive = new JsonObject
        {
            ["characters"] = new JsonArray(new JsonObject { ["poolId"]="special-1", ["poolName"]="热烈色彩", ["charName"]="伊冯", ["rarity"]=6, ["seqId"]=1 }),
            ["weapons"] = new JsonArray(
                Weapon("绘涂申领", "艺术暴君（手铳）", 1, 6),
                Weapon("绘涂申领", "楔子", 2, 6),
                Weapon("其他申领", "艺术暴君", 3, 6),
                Weapon("绘涂申领", "艺术暴君", 4, 6, "gift"))
        };
        var result = await Analyze(archive);
        var pool = Assert.Single(result.Pools);
        Assert.Equal("2026/02/24 12:00 — 2026/03/12 06:00", pool.DateRangeText);
        var weapon = Assert.IsType<Core.EndfieldSignatureWeaponAnalysis>(pool.SignatureWeapon);
        Assert.Equal(2, weapon.RecordedWeapons); Assert.Equal(1, weapon.FeaturedCount);
        Assert.Equal(2, weapon.SixStars.Count);
        Assert.Equal("第 2 件记录", weapon.SixStars[0].PositionText);
    }
    [Fact]
    public async Task RefactorWeaponsAndDatesRemainPhaseSpecific()
    {
        var archive = new JsonObject { ["characters"] = new JsonArray(
            new JsonObject { ["poolId"]="rerun_chr_1", ["poolName"]="绚丽异彩", ["poolVersion"]="1", ["rarity"]=6, ["seqId"]=1, ["gachaTs"]=1 },
            new JsonObject { ["poolId"]="rerun_chr_1", ["poolName"]="绚丽异彩", ["poolVersion"]="2", ["rarity"]=6, ["seqId"]=2, ["gachaTs"]=2 }),
            ["weapons"] = new JsonArray(Weapon("点绘申领", "艺术暴君", 1, 6, phase:"1"), Weapon("点绘申领", "艺术暴君", 2, 6, phase:"2")) };
        var current = Assert.Single((await Analyze(archive)).Pools);
        Assert.Empty(current.DateRangeText);
        Assert.Equal(1, current.SignatureWeapon!.RecordedWeapons);
        var previous = Assert.Single(current.PreviousPhases!);
        Assert.Contains("2026/09/24", previous.DateRangeText);
        Assert.Equal(1, previous.SignatureWeapon!.RecordedWeapons);
    }
    [Fact]
    public async Task UnknownPoolDoesNotInventWeaponOrDates()
    {
        var result = await Analyze(new JsonObject { ["characters"] = new JsonArray(new JsonObject { ["poolId"]="unknown", ["poolName"]="未来寻访", ["rarity"]=6 }) });
        var pool = Assert.Single(result.Pools); Assert.Null(pool.SignatureWeapon); Assert.Empty(pool.DateRangeText);
    }
    [Fact]
    public async Task VersionStartAndMaintenanceEndUseAbsoluteBeijingTime()
    {
        var result = await Analyze(new JsonObject { ["characters"] = new JsonArray(
            new JsonObject { ["poolId"]="special-1", ["poolName"]="熔火灼痕", ["rarity"]=6 },
            new JsonObject { ["poolId"]="special-2", ["poolName"]="拳出无悔", ["rarity"]=6 },
            new JsonObject { ["poolId"]="special-3", ["poolName"]="晨星于此闪耀", ["rarity"]=6 }) });
        Assert.Equal("2026/01/22 11:00 — 2026/02/07 11:59", result.Pools.Single(p => p.PoolName == "熔火灼痕").DateRangeText);
        Assert.Equal("2026/06/05 12:00 — 2026/06/26 11:59", result.Pools.Single(p => p.PoolName == "拳出无悔").DateRangeText);
        Assert.Equal("2026/08/09 12:00 — 2026/09/02 06:00", result.Pools.Single(p => p.PoolName == "晨星于此闪耀").DateRangeText);
    }
    [Fact]
    public void OperatorAndWeaponTimestampsUseSameTimezone()
    {
        var time = new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
        var character = new Core.EndfieldSixStarPull("伊冯", "绚丽异彩", 1, 1, false, time);
        var weapon = new Core.EndfieldWeaponResult("艺术暴君", true, 1, time);
        Assert.Equal("2026/09/24 12:00", character.ObtainedAtText);
        Assert.Equal(character.ObtainedAtText, weapon.DateText);
    }
    private static JsonObject Weapon(string pool, string name, int sequence, int rarity, string kind="draw", string? phase=null) =>
        new() { ["poolId"]=pool, ["poolName"]=pool, ["weaponName"]=name, ["seqId"]=sequence, ["rarity"]=rarity, ["kind"]=kind, ["poolVersion"]=phase, ["gachaTs"]=1700000000 + sequence };
    private async Task<Core.EndfieldGachaAnalysis> Analyze(JsonObject archive)
    {
        var file = Path.Combine(_root, "input.json"); await File.WriteAllTextAsync(file, archive.ToJsonString());
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);
        Assert.True((await service.ImportAsync(file)).Success); return await service.GetAnalysisAsync();
    }
    public void Dispose() { Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _original); Directory.Delete(_root, true); }
}