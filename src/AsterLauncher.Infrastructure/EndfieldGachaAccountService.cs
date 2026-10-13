using System.Text.Json;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class EndfieldGachaAccountService : IEndfieldGachaAccountService, IDisposable
{
    internal const string LegacyAccountId = "legacy";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _gachaRoot;
    private readonly string _indexPath;
    private readonly Func<string, IEndfieldGachaArchiveService> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AccountArchive> _archives = [];
    private IReadOnlyList<EndfieldGachaAccount> _accounts = [new(LegacyAccountId, "原有账号档案", true)];
    private string _selectedId = LegacyAccountId;
    private bool _loaded;
    private string? _indexFingerprint;

    private sealed record AccountIndex(int Version, string SelectedAccountId, IReadOnlyList<EndfieldGachaAccount> Accounts);
    private sealed record AccountArchive(IEndfieldGachaArchiveService Service, SemaphoreSlim Operations);

    public EndfieldGachaAccountService(ILogger<EndfieldGachaArchiveService> logger, IResourceCatalogProvider resources)
        : this(LauncherDataPaths.ResolveDataDirectory(), path => new EndfieldGachaArchiveService(logger, resources, path)) { }

    internal EndfieldGachaAccountService(string dataRoot, Func<string, IEndfieldGachaArchiveService> factory)
    {
        _gachaRoot = Path.GetFullPath(Path.Combine(dataRoot, "gacha"));
        _indexPath = Path.Combine(_gachaRoot, "endfield-accounts.json");
        _factory = factory;
    }

    public string SelectedAccountId => Volatile.Read(ref _selectedId);
    public string ArchivePath => ResolveArchivePath(SelectedAccountId);

    public async Task<IReadOnlyList<EndfieldGachaAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _accounts.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!_accounts.Any(account => account.Id == accountId)) throw new InvalidOperationException("账号档案不存在，请重新选择。");
            if (_selectedId == accountId) return;
            await SaveIndexAsync(new(1, accountId, _accounts), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _selectedId, accountId);
        }
        finally { _gate.Release(); }
    }

    public async Task<EndfieldGachaAccount> CreateAccountAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_accounts.Count >= 100) throw new InvalidOperationException("本地账号档案已达到数量上限。");
            if (_accounts.Any(account => account.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名账号档案，请使用不同的名称。");
            var account = new EndfieldGachaAccount(Guid.NewGuid().ToString("N"), name, false);
            var accounts = _accounts.Append(account).ToArray();
            await SaveIndexAsync(new(1, _selectedId, accounts), cancellationToken).ConfigureAwait(false);
            _accounts = accounts;
            return account;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (_loaded) return;
        EnsureOwnedPath(_indexPath);
        if (File.Exists(_indexPath))
        {
            try
            {
                if (new FileInfo(_indexPath).Length > 65536) throw new InvalidDataException();
                await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _indexFingerprint = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                stream.Position = 0;
                var index = await JsonSerializer.DeserializeAsync<AccountIndex>(stream, JsonOptions, token).ConfigureAwait(false);
                if (index is null || index.Version != 1 || index.Accounts is null || index.Accounts.Count is < 1 or > 100
                    || index.Accounts.Any(account => account is null)
                    || index.Accounts.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != index.Accounts.Count
                    || index.Accounts.Select(account => account.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Accounts.Count
                    || index.Accounts.Count(account => account.Id == LegacyAccountId && account.IsLegacy) != 1
                    || !index.Accounts.Any(account => account.Id == index.SelectedAccountId))
                    throw new InvalidDataException();
                foreach (var account in index.Accounts)
                {
                    if (account.Name != ValidateName(account.Name)
                        || (account.Id == LegacyAccountId) != account.IsLegacy
                        || account.Id != LegacyAccountId && (!Guid.TryParseExact(account.Id, "N", out _) || account.Id != account.Id.ToLowerInvariant()))
                        throw new InvalidDataException();
                    EnsureOwnedPath(ResolveArchivePath(account.Id));
                }
                _accounts = index.Accounts.ToArray();
                Volatile.Write(ref _selectedId, index.SelectedAccountId);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException("终末地账号档案列表无法读取，原文件已保留。请检查数据目录后重试。");
            }
        }
        _loaded = true;
    }

    private async Task SaveIndexAsync(AccountIndex index, CancellationToken token)
    {
        EnsureOwnedPath(_indexPath);
        Directory.CreateDirectory(_gachaRoot);
        var lockPath = Path.Combine(_gachaRoot, ".endfield-accounts.lock");
        EnsureOwnedPath(lockPath);
        // File sharing provides an inter-process commit lock. Delete-on-close leaves no extra history.
        await using var commitLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        if (File.Exists(_indexPath) && new FileInfo(_indexPath).Length > 65536)
            throw new InvalidDataException("账号档案列表过大，原文件已保留。");
        var currentFingerprint = File.Exists(_indexPath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_indexPath, token).ConfigureAwait(false)))
            : null;
        if (currentFingerprint != _indexFingerprint)
        {
            _loaded = false;
            throw new InvalidOperationException("账号列表已在其他窗口变化，请重新打开抽卡页后再选择。");
        }
        Directory.CreateDirectory(_gachaRoot);
        var temporary = Path.Combine(_gachaRoot, ".endfield-accounts-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            EnsureOwnedPath(_indexPath);
            var savedFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false)));
            if (File.Exists(_indexPath)) File.Replace(temporary, _indexPath, null);
            else File.Move(temporary, _indexPath);
            _indexFingerprint = savedFingerprint;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string ResolveArchivePath(string id) => id == LegacyAccountId
        ? Path.Combine(_gachaRoot, "endfield-records.json")
        : Guid.TryParseExact(id, "N", out _) ? Path.Combine(_gachaRoot, "endfield-accounts", id + ".json")
        : throw new InvalidOperationException("账号档案标识无效。");

    private void EnsureOwnedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_gachaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("账号档案路径无效。");
        for (var cursor = full; cursor.Length > Path.GetPathRoot(full)!.Length; cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("账号档案不支持链接文件或目录，请保留原文件后检查路径。");

        }
    }

    private static string ValidateName(string name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 40 || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal)
            || value.Contains("authkey", StringComparison.OrdinalIgnoreCase) || value.Contains("token", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请输入 1–40 字的本地账号名称。");
        return value;
    }

    private async Task<T> UseAsync<T>(Func<IEndfieldGachaArchiveService, Task<T>> action, CancellationToken token, bool mutates = false)
    {
        AccountArchive archive;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            var id = SelectedAccountId;
            var path = ResolveArchivePath(id);
            EnsureOwnedPath(path);
            if (!_archives.TryGetValue(id, out archive!))
                _archives.Add(id, archive = new(_factory(path), new(1, 1)));
        }
        finally { _gate.Release(); }
        await archive.Operations.WaitAsync(token).ConfigureAwait(false);
        FileStream? operationLock = null;
        try
        {
            if (mutates)
            {
                var path = archive.Service.ArchivePath;
                var lockPath = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".lock");
                EnsureOwnedPath(path);
                EnsureOwnedPath(lockPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            return await action(archive.Service).ConfigureAwait(false);
        }
        finally { operationLock?.Dispose(); archive.Operations.Release(); }
    }

    public Task<EndfieldGachaSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
        => UseAsync(service => service.GetSummaryAsync(cancellationToken), cancellationToken);
    public Task<EndfieldGachaAnalysis> GetAnalysisAsync(CancellationToken cancellationToken = default)
        => UseAsync(service => service.GetAnalysisAsync(cancellationToken), cancellationToken);
    public Task<GachaImportResult> CaptureFromUrlAsync(string historyUrl, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, bool fullRefresh = false)
        => UseAsync(service => service.CaptureFromUrlAsync(historyUrl, progress, cancellationToken, fullRefresh), cancellationToken, mutates: true);
    public Task<GachaImportResult> CaptureFromGameAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, bool fullRefresh = false)
        => UseAsync(service => service.CaptureFromGameAsync(progress, cancellationToken, fullRefresh), cancellationToken, mutates: true);
    public Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        => UseAsync(service => service.ImportAsync(sourcePath, cancellationToken), cancellationToken, mutates: true);
    public Task<GachaImportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
        => UseAsync(service => service.ExportJsonAsync(destinationPath, cancellationToken), cancellationToken, mutates: true);
    public Task<GachaImportResult> ExportCsvAsync(string destinationPath, CancellationToken cancellationToken = default)
        => UseAsync(service => service.ExportCsvAsync(destinationPath, cancellationToken), cancellationToken, mutates: true);

    public void Dispose()
    {
        foreach (var archive in _archives.Values)
        {
            if (archive.Service is IDisposable disposable) disposable.Dispose();
            archive.Operations.Dispose();
        }
        _gate.Dispose();
    }
}
