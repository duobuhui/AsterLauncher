using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class EndfieldGachaAccountTests : IDisposable
{
    private readonly string _original = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME") ?? "";
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.Tests", "endfield-accounts-" + Guid.NewGuid().ToString("N"));
    public EndfieldGachaAccountTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }
    private EndfieldGachaAccountService Service() => new(_root,
        path => new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, new Resources(), path));
    private sealed class Resources : IResourceCatalogProvider
    {
        public ResourceCatalog Current { get; } = new();
        public string? FindImage(string gameId, string kind, string key) => null;
        public event EventHandler? Updated { add { } remove { } }
    }
    private string Legacy => Path.Combine(_root, "gacha", "endfield-records.json");
    private string Index => Path.Combine(_root, "gacha", "endfield-accounts.json");
    private async Task<string> Sample(string name, int sequence)
    {
        var path = Path.Combine(_root, name + ".json");
        await File.WriteAllTextAsync(path, $$"""{"characters":[{"seqId":{{sequence}},"poolId":"standard","poolName":"基础寻访","kind":"draw","rarity":6,"charName":"余烬"}],"weapons":[]}""");
        return path;
    }
    [Fact]
    public async Task ExistingArchiveRemainsAtOriginalPathAndIsNotRewrittenBySelection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Legacy)!);
        var original = """{"characters":[{"seqId":1,"kind":"draw","poolId":"standard","rarity":6}],"weapons":[]}""";
        await File.WriteAllTextAsync(Legacy, original);
        using var service = Service();
        var first = Assert.Single(await service.GetAccountsAsync());
        Assert.True(first.IsLegacy);
        Assert.Equal(Legacy, service.ArchivePath);
        Assert.Equal(1, (await service.GetSummaryAsync()).CharacterCount);
        var other = await service.CreateAccountAsync("B服小号");
        await service.SelectAccountAsync(other.Id);
        Assert.Equal(0, (await service.GetSummaryAsync()).TotalCount);
        await service.SelectAccountAsync(first.Id);
        Assert.Equal(original, await File.ReadAllTextAsync(Legacy));
    }
    [Fact]
    public async Task ImportsWithIdenticalRecordIdsAreIsolatedAndSelectionSurvivesRestart()
    {
        using var service = Service();
        var input = await Sample("sample", 1);
        Assert.True((await service.ImportAsync(input)).Success);
        var account = await service.CreateAccountAsync("另一个账号");
        await service.SelectAccountAsync(account.Id);
        Assert.True((await service.ImportAsync(input)).Success);
        Assert.Equal(1, (await service.GetSummaryAsync()).TotalCount);
        using var restarted = Service();
        await restarted.GetAccountsAsync();
        Assert.Equal(account.Id, restarted.SelectedAccountId);
        Assert.Equal(1, (await restarted.GetSummaryAsync()).TotalCount);
        await restarted.SelectAccountAsync("legacy");
        Assert.Equal(1, (await restarted.GetSummaryAsync()).TotalCount);
    }
    [Fact]
    public async Task ExportUsesOnlySelectedAccount()
    {
        using var service = Service();
        Assert.True((await service.ImportAsync(await Sample("first", 1))).Success);
        var account = await service.CreateAccountAsync("新账号");
        await service.SelectAccountAsync(account.Id);
        Assert.True((await service.ImportAsync(await Sample("second", 2))).Success);
        var exported = Path.Combine(_root, "exported.json");
        Assert.True((await service.ExportJsonAsync(exported)).Success);
        var text = await File.ReadAllTextAsync(exported);
        Assert.Contains("\"seqId\": 2", text);
        Assert.DoesNotContain("\"seqId\": 1", text);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/?token=private")]
    [InlineData("u8_token=private")]
    [InlineData("line\nbreak")]
    [InlineData("12345678901234567890123456789012345678901")]
    public async Task InvalidNamesDoNotCreateMetadata(string name)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAccountAsync(name));
        Assert.False(File.Exists(Index));
    }
    [Fact]
    public async Task NamesCannotBeDuplicatedAndIdentifiersCannotEscapeArchiveDirectory()
    {
        using var service = Service();
        await service.CreateAccountAsync("账号");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAccountAsync(" 账号 "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAccountAsync("../outside"));
        Assert.Equal("legacy", service.SelectedAccountId);
    }
    [Fact]
    public async Task DamagedIndexIsPreservedRatherThanReplacedWithDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Index)!);
        const string broken = """{"version":1,"accounts":""";
        await File.WriteAllTextAsync(Index, broken);
        using var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAccountsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAccountAsync("另一个"));
        Assert.Equal(broken, await File.ReadAllTextAsync(Index));
    }
    [Fact]
    public async Task OtherWindowChangesAreNotOverwritten()
    {
        using var first = Service();
        using var second = Service();
        await first.GetAccountsAsync();
        await second.GetAccountsAsync();
        await first.CreateAccountAsync("第一个");
        var saved = await File.ReadAllTextAsync(Index);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.CreateAccountAsync("第二个"));
        Assert.Equal(saved, await File.ReadAllTextAsync(Index));
        Assert.Equal(2, (await second.GetAccountsAsync()).Count);
    }
    [Fact]
    public async Task InFlightImportKeepsItsCapturedAccountWhenSelectionChanges()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? written = null;
        using var service = new EndfieldGachaAccountService(_root, path => new DelayedArchive(path, entered, complete, value => written = value));
        var other = await service.CreateAccountAsync("另一个账号");
        var importing = service.ImportAsync("unused");
        await entered.Task;
        await service.SelectAccountAsync(other.Id);
        complete.SetResult();
        await importing;
        Assert.Equal(Legacy, written);
        Assert.NotEqual(Legacy, service.ArchivePath);
    }
    [Fact]
    public async Task ConcurrentIndexCommitCannotOverwriteExistingAccountList()
    {
        using var service = Service();
        await service.CreateAccountAsync("原账号");
        var original = await File.ReadAllTextAsync(Index);
        var lockPath = Path.Combine(_root, "gacha", ".endfield-accounts.lock");
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            await Assert.ThrowsAnyAsync<IOException>(() => service.CreateAccountAsync("冲突账号"));
        Assert.Equal(original, await File.ReadAllTextAsync(Index));
        Assert.False(File.Exists(lockPath));
    }
    [Fact]
    public async Task ConcurrentArchiveImportIsRejectedWithoutChangingOriginalRecords()
    {
        using var service = Service();
        Assert.True((await service.ImportAsync(await Sample("source", 1))).Success);
        var original = await File.ReadAllTextAsync(Legacy);
        var lockPath = Path.Combine(Path.GetDirectoryName(Legacy)!, "." + Path.GetFileName(Legacy) + ".lock");
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            await Assert.ThrowsAnyAsync<IOException>(() => service.ImportAsync("unused"));
        Assert.Equal(original, await File.ReadAllTextAsync(Legacy));
        Assert.False(File.Exists(lockPath));
    }
    private sealed class DelayedArchive(string path, TaskCompletionSource entered, TaskCompletionSource complete, Action<string> saved) : IEndfieldGachaArchiveService
    {
        public string ArchivePath => path;
        public async Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            entered.SetResult();
            await complete.Task.WaitAsync(cancellationToken);
            saved(path);
            return new(true, 1, "ok");
        }
        public Task<EndfieldGachaSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EndfieldGachaSummary(0, 0, null));
        public Task<EndfieldGachaAnalysis> GetAnalysisAsync(CancellationToken cancellationToken = default) => Task.FromResult(EndfieldGachaAnalysis.Empty);
        public Task<GachaImportResult> CaptureFromUrlAsync(string historyUrl, IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool fullRefresh = false) => throw new NotImplementedException();
        public Task<GachaImportResult> CaptureFromGameAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool fullRefresh = false) => throw new NotImplementedException();
        public Task<GachaImportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<GachaImportResult> ExportCsvAsync(string destinationPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _original);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
