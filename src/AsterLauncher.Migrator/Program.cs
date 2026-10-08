using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace AsterLauncher.Migrator;

internal static class Program
{
    private const string EntryName = "AsterLauncher.exe";
    private const string AppName = "App";
    private const string ToolsName = "MigrationTools";
    private const string ManifestName = "release-files.json";
    private const string CompanionManifest = "App/install-manifest.json";
    private const string DataPointerName = "asterlauncher.data-location.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                if (string.Equals(Path.GetFileName(Environment.ProcessPath), "AsterLauncher.Migrator.exe", StringComparison.OrdinalIgnoreCase))
                    RunInteractiveMigration();
                else LaunchInstalledApp();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--sync-release")
            {
                SynchronizeReleaseManifest(Path.GetFullPath(args[1]));
                PruneCompletedBackups(Path.GetFullPath(args[1]));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--recover")
            {
                Recover(args[1]);
                return 0;
            }
            if (args.Length != 7 || args[0] != "--migrate" || args[2] != "--package"
                || args[4] != "--sha256" || args[6] != "--no-launch")
                throw new ArgumentException("用法: AsterLauncher.Migrator.exe --migrate <旧版目录> --package <多文件 ZIP> --sha256 <ZIP SHA-256> --no-launch");
            Migrate(args[1], args[3], args[5]);
            return 0;
        }
        catch (Exception exception)
        {
            var message = "AsterLauncher 迁移失败：" + exception.Message;
            try { Console.Error.WriteLine(message); } catch { /* WinExe has no console. */ }
            if (args.Length >= 2 && (args[0] == "--migrate" || args[0] == "--recover"))
            {
                try { File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "migration-error.log"), message); }
                catch { /* Preserve the original failure. */ }
            }
            if (args.Length == 0) MessageBoxW(IntPtr.Zero, message, "AsterLauncher", 0x10);
            return 1;
        }
    }

    private static void RunInteractiveMigration()
    {
        Application.EnableVisualStyles();
        using var folder = new FolderBrowserDialog
        {
            Description = "选择现有 AsterLauncher 安装目录（内有旧版 AsterLauncher.exe）",
            UseDescriptionForTitle = true,
            SelectedPath = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "E:\\"
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        var root = Path.GetFullPath(folder.SelectedPath);
        if (File.Exists(Path.Combine(root, ".aster-migration.json")))
        {
            if (MessageBox.Show("发现未完成的迁移记录。现在恢复旧版入口和目录？", "AsterLauncher 迁移",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Recover(root);
            MessageBox.Show("旧版入口已恢复，数据目录未更改。恢复的新文件留在暂存目录供检查。", "AsterLauncher 迁移",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var picker = new OpenFileDialog
        {
            Title = "选择 AsterLauncher 多文件 ZIP",
            Filter = "ZIP 发布包 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (picker.ShowDialog() != DialogResult.OK) return;
        var package = Path.GetFullPath(picker.FileName);
        var hashFile = package + ".sha256";
        if (!File.Exists(hashFile))
            throw new FileNotFoundException("请把发布包对应的 .zip.sha256 文件放在 ZIP 旁。", hashFile);
        var hash = File.ReadAllText(hashFile).Trim();
        if (!IsSha256(hash)) throw new InvalidDataException(".sha256 文件内容无效。");
        if (MessageBox.Show("将校验发布包，再把新版放入 App 文件夹。旧版程序会保存在 MigrationBackup；现有 Data 不移动。继续？",
                "AsterLauncher 迁移", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        using var progress = new Form
        {
            Text = "AsterLauncher 正在迁移",
            Width = 490,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ControlBox = false,
            BackColor = System.Drawing.Color.FromArgb(22, 28, 49),
            ForeColor = System.Drawing.Color.White
        };
        var label = new Label
        {
            Text = "正在校验并暂存文件，请勿关闭窗口。",
            Left = 25,
            Top = 22,
            Width = 425,
            Height = 27
        };
        var bar = new ProgressBar
        {
            Left = 25,
            Top = 61,
            Width = 425,
            Height = 18,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25
        };
        progress.Controls.Add(label);
        progress.Controls.Add(bar);
        progress.Shown += async (_, _) =>
        {
            try
            {
                await Task.Run(() => Migrate(root, package, hash));
                progress.Hide();
                MessageBox.Show("迁移完成。请从原安装目录的 AsterLauncher.exe 启动；旧版 EXE 保存在 MigrationBackup。",
                    "AsterLauncher 迁移", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                progress.Hide();
                try { File.WriteAllText(Path.Combine(root, "migration-error.log"), exception.Message); }
                catch { /* Preserve the original error in the dialog. */ }
                MessageBox.Show("迁移失败：" + exception.Message + "\n旧版程序和数据已保留。",
                    "AsterLauncher 迁移", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { progress.Close(); }
        };
        Application.Run(progress);
    }
    private static void LaunchInstalledApp()
    {
        var root = Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException("无法定位启动器目录。");
        SynchronizeReleaseManifest(root);
        PruneCompletedBackups(root);
        var app = Path.Combine(root, AppName, EntryName);
        if (!File.Exists(app)) throw new FileNotFoundException("App 文件夹中的启动器缺失，请从迁移备份恢复。", app);
        var info = new ProcessStartInfo(app)
        {
            WorkingDirectory = Path.GetDirectoryName(app)!,
            UseShellExecute = false
        };
        info.Environment["ASTERLAUNCHER_DATA_HOME"] = ResolveDataDirectory(root);
        Process.Start(info)?.Dispose();
    }

    // 0.1.3 invokes its old migrator, which did not install the root manifest.
    // The companion survives that migration; verify its files before restoring the manifest.
    private static void SynchronizeReleaseManifest(string root)
    {
        var companion = Path.Combine(root, CompanionManifest);
        if (!File.Exists(companion)) return; // Earlier packages have no companion.
        var target = Path.Combine(root, ManifestName);
        var cursor = companion;
        while (cursor is not null)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && IsReparse(cursor))
                throw new InvalidDataException("发布清单路径包含文件系统链接。");
            cursor = Path.GetDirectoryName(cursor);
        }
        if (File.Exists(target) && IsReparse(target)) throw new InvalidDataException("发布清单是文件链接。");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(companion), JsonOptions)
            ?? throw new InvalidDataException("安装清单无效。");
        if (manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null
            || manifest.Files.Count == 0 || manifest.Files.Count > 10000 || manifest.Files.ContainsKey(CompanionManifest)
            || !manifest.Files.ContainsKey("App/AsterLauncher.exe") || !manifest.Files.ContainsKey(EntryName))
            throw new InvalidDataException("安装清单不完整。");
        var companionSpec = new FileSpecification(new FileInfo(companion).Length, HashFile(companion));
        if (File.Exists(target))
        {
            try
            {
                var current = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(target), JsonOptions);
                if (current?.Version == manifest.Version && current.Files is not null
                    && current.Files.TryGetValue(CompanionManifest, out var prior) && prior == companionSpec) return;
            }
            catch (JsonException) { /* Rebuild from the verified companion. */ }
        }
        foreach (var (name, specification) in manifest.Files)
        {
            if (!SafeEntry(name) || !(name.StartsWith("App/", StringComparison.Ordinal)
                || name.StartsWith("MigrationTools/", StringComparison.Ordinal) || name == EntryName)
                || specification is null || !IsSha256(specification.Sha256))
                throw new InvalidDataException("安装清单路径无效。");
            var file = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            for (var parent = file; parent is not null && IsWithin(root, parent); parent = Path.GetDirectoryName(parent))
                if ((File.Exists(parent) || Directory.Exists(parent)) && IsReparse(parent))
                    throw new InvalidDataException("安装文件路径包含文件系统链接。");
            if (!File.Exists(file) || new FileInfo(file).Length != specification.Length
                || !HashFile(file).Equals(specification.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("升级后文件校验失败：" + name);
        }
        manifest.Files.Add(CompanionManifest, companionSpec);
        var temporary = Path.Combine(root, ".release-files-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string ResolveDataDirectory(string root)
    {
        var pointer = Path.Combine(root, DataPointerName);
        if (File.Exists(pointer))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pointer));
            var selected = document.RootElement.GetProperty("directory").GetString();
            if (string.IsNullOrWhiteSpace(selected) || !Path.IsPathFullyQualified(selected))
                throw new InvalidDataException("原有数据位置文件无效，请检查 " + pointer);
            return Path.GetFullPath(selected);
        }
        var environment = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
        return string.IsNullOrWhiteSpace(environment) ? Path.Combine(root, "Data") : Path.GetFullPath(environment);
    }

    private static void Migrate(string rootArgument, string packageArgument, string expectedHash)
    {
        var root = Path.GetFullPath(rootArgument);
        var package = Path.GetFullPath(packageArgument);
        if (!Directory.Exists(root) || IsReparse(root)) throw new DirectoryNotFoundException("旧版目录不存在或是目录链接。" + root);
        var oldEntry = Path.Combine(root, EntryName);
        if (!File.Exists(oldEntry) || IsReparse(oldEntry)) throw new FileNotFoundException("旧版启动器不存在或是文件链接。", oldEntry);
        if (!File.Exists(package) || IsReparse(package)) throw new FileNotFoundException("多文件包不存在或是文件链接。", package);
        if (!IsSha256(expectedHash)) throw new ArgumentException("ZIP SHA-256 必须是 64 位十六进制值。");
        if (!HashFile(package).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("多文件包 SHA-256 不匹配，未修改旧版安装。");
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位迁移程序。");
        if (Path.GetFullPath(self).Equals(oldEntry, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请运行独立的 AsterLauncher.Migrator.exe，不要让入口程序替换自身。");
        if (IsReparse(self)) throw new InvalidOperationException("迁移程序不能从文件链接运行。");
        if (!string.Equals(Path.GetPathRoot(self), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("迁移程序和旧版目录须位于同一卷，请先将迁移程序复制到旧版目录旁。");
        if (!string.Equals(Path.GetPathRoot(package), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("多文件包和旧版目录须位于同一卷，以便安全暂存和切换。");
        if (IsWithin(Path.Combine(root, AppName), self) || IsWithin(Path.Combine(root, ToolsName), self))
            throw new InvalidOperationException("迁移程序不能放在将被替换的 App 子目录中。");
        if (IsWithin(root, package) && package.StartsWith(Path.Combine(root, AppName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("多文件包不能放在将被替换的 App 子目录中。");
        ResolveDataDirectory(root); // Fail before any mutation if the existing pointer is invalid.
        EnsureNotRunning(oldEntry, Path.Combine(root, AppName, EntryName));
        var existingApp = Path.Combine(root, AppName);
        var existingTools = Path.Combine(root, ToolsName);
        var existingManifest = Path.Combine(root, ManifestName);
        if (File.Exists(existingManifest) && IsReparse(existingManifest)) throw new InvalidOperationException("现有发布清单是文件链接，拒绝覆盖。");
        if (Directory.Exists(existingApp) && IsReparse(existingApp)) throw new InvalidOperationException("现有 App 是目录链接，拒绝覆盖。");
        if (Directory.Exists(existingTools) && IsReparse(existingTools)) throw new InvalidOperationException("现有 MigrationTools 是目录链接，拒绝覆盖。");
        var nonce = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stage = Path.Combine(root, ".aster-migration-" + nonce);
        var backupRoot = Path.Combine(root, "MigrationBackup");
        var backupEntry = Path.Combine(backupRoot, "AsterLauncher-" + nonce + ".exe");
        var backupApp = Path.Combine(backupRoot, "App-" + nonce);
        var backupTools = Path.Combine(backupRoot, "MigrationTools-" + nonce);
        var backupManifest = Path.Combine(backupRoot, "release-files-" + nonce + ".json");
        var candidate = Path.Combine(stage, EntryName);
        var journal = Path.Combine(root, ".aster-migration.json");
        if (File.Exists(journal)) throw new InvalidOperationException("发现未完成的迁移记录。请先检查 .aster-migration.json 和 MigrationBackup，勿覆盖旧版文件。");
        Directory.CreateDirectory(stage);
        var committed = false;
        try
        {
            ExtractAndVerify(package, stage);
            if (!File.Exists(candidate)) throw new InvalidDataException("发布包缺少顶层入口。");
            Directory.CreateDirectory(backupRoot);
            var record = new MigrationRecord(nonce, backupEntry, backupApp, backupTools, stage, HashFile(candidate), expectedHash.ToLowerInvariant(), Directory.Exists(existingApp), Directory.Exists(existingTools), HashFile(Path.Combine(stage, ManifestName)), File.Exists(existingManifest));
            File.WriteAllText(journal, JsonSerializer.Serialize(record, JsonOptions));
            var appMoved = false;
            var oldMoved = false;
            var toolsMoved = false;
            var newToolsMoved = false;
            var newAppMoved = false;
            var shimMoved = false;
            var manifestMoved = false;
            var newManifestMoved = false;
            try
            {
                if (Directory.Exists(existingApp)) { Directory.Move(existingApp, backupApp); appMoved = true; }
                Directory.Move(Path.Combine(stage, AppName), existingApp); newAppMoved = true;
                if (Directory.Exists(existingTools)) { Directory.Move(existingTools, backupTools); toolsMoved = true; }
                Directory.Move(Path.Combine(stage, ToolsName), existingTools); newToolsMoved = true;
                File.Move(oldEntry, backupEntry); oldMoved = true;
                File.Move(candidate, oldEntry); shimMoved = true;
                if (File.Exists(existingManifest)) { File.Move(existingManifest, backupManifest); manifestMoved = true; }
                File.Move(Path.Combine(stage, ManifestName), existingManifest); newManifestMoved = true;
                File.WriteAllText(Path.Combine(backupRoot, "migration-" + nonce + ".json"),
                    JsonSerializer.Serialize(record, JsonOptions));
                File.Delete(journal);
                committed = true;
                try
                {
                    var errorLog = Path.Combine(root, "migration-error.log");
                    if (File.Exists(errorLog)) File.Delete(errorLog);
                }
                catch (IOException) { /* A stale diagnostic does not invalidate a committed migration. */ }
            }
            catch
            {
                if (newManifestMoved && File.Exists(existingManifest)) File.Move(existingManifest, Path.Combine(stage, "FailedManifest.json"));
                if (manifestMoved && !File.Exists(existingManifest)) File.Move(backupManifest, existingManifest);
                if (shimMoved && File.Exists(oldEntry) && HashFile(oldEntry).Equals(record.ShimSha256, StringComparison.OrdinalIgnoreCase))
                    File.Delete(oldEntry);
                if (oldMoved && !File.Exists(oldEntry)) File.Move(backupEntry, oldEntry);
                if (newToolsMoved && Directory.Exists(existingTools)) Directory.Move(existingTools, Path.Combine(stage, "FailedTools"));
                if (toolsMoved && !Directory.Exists(existingTools)) Directory.Move(backupTools, existingTools);
                if (newAppMoved && Directory.Exists(existingApp)) Directory.Move(existingApp, Path.Combine(stage, "FailedApp"));
                if (appMoved && !Directory.Exists(existingApp)) Directory.Move(backupApp, existingApp);
                if (File.Exists(journal)) File.Delete(journal);
                throw;
            }
            PruneCompletedBackups(root, nonce);
            Console.WriteLine("更新完成。仅保留最近一次可回滚的程序备份：" + backupEntry);
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            if (!File.Exists(journal) && Directory.Exists(stage) && IsWithin(root, stage) && !IsReparse(stage))
            {
                if (!committed || !Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage, true);
            }
        }
    }

    // A receipt is written only after every installed component has been switched.
    // Old clients still invoke their old helper, so the new root entry also applies
    // this policy on the first launch after an update.
    private static void PruneCompletedBackups(string root, string? preferredId = null)
    {
        try
        {
            root = Path.GetFullPath(root);
            var backupRoot = Path.Combine(root, "MigrationBackup");
            if (!Directory.Exists(backupRoot) || HasReparseAncestor(backupRoot)
                || File.Exists(Path.Combine(root, ".aster-migration.json"))
                || Directory.Exists(Path.Combine(root, ".aster-migration.json"))) return;

            // Protect every configured data location, including one temporarily
            // overridden by the development environment.
            var protectedPaths = new List<string> { Path.Combine(root, "Data") };
            var environment = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(environment)) protectedPaths.Add(Path.GetFullPath(environment));
            var pointer = Path.Combine(root, DataPointerName);
            if (File.Exists(pointer))
            {
                if (HasReparseAncestor(pointer)) return;
                using var document = JsonDocument.Parse(File.ReadAllText(pointer));
                var selected = document.RootElement.GetProperty("directory").GetString();
                if (string.IsNullOrWhiteSpace(selected) || !Path.IsPathFullyQualified(selected)) return;
                protectedPaths.Add(Path.GetFullPath(selected));
            }

            if (protectedPaths.Any(HasReparseAncestor)) return; // A data alias may resolve inside a backup.
            var receipts = Directory.GetFiles(backupRoot, "migration-*.json", SearchOption.TopDirectoryOnly);
            if (receipts.Length < 2) return;
            var groups = receipts.Select(receipt => ReadCompletedBackup(root, receipt, protectedPaths))
                .Where(group => group is not null).Cast<CompletedBackup>()
                .OrderByDescending(group => group.Id == preferredId)
                .ThenByDescending(group => group.CompletedAtUtc)
                .ThenByDescending(group => group.Id, StringComparer.Ordinal)
                .ToArray();
            if (groups.Length < 2) return;
            foreach (var obsolete in groups.Skip(1))
            {
                if (File.Exists(Path.Combine(root, ".aster-migration.json"))
                    || Directory.Exists(Path.Combine(root, ".aster-migration.json"))) return;
                // Recheck immediately before removal. Delete only listed files and
                // empty directories; never recursively remove an untrusted tree.
                var current = ReadCompletedBackup(root, obsolete.Receipt, protectedPaths);
                if (current is null) continue;
                try
                {
                    foreach (var file in current.Files)
                    {
                        if (HasReparseAncestor(file)) throw new IOException("备份路径已改变。");
                        File.Delete(file);
                    }
                    foreach (var directory in current.Directories.OrderByDescending(path => path.Length))
                    {
                        if (HasReparseAncestor(directory)) throw new IOException("备份路径已改变。");
                        Directory.Delete(directory, false);
                    }
                    if (HasReparseAncestor(current.Receipt)) throw new IOException("备份路径已改变。");
                    File.Delete(current.Receipt);
                }
                catch (IOException) { /* Keep the receipt and any remaining files for manual inspection. */ }
                catch (UnauthorizedAccessException) { /* Do not invalidate a successful update. */ }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or InvalidOperationException
            or NotSupportedException or KeyNotFoundException)
        {
            // Cleanup is optional. Unknown or interrupted backups must never stop
            // the installed application from starting or undo a committed update.
        }
    }

    private static CompletedBackup? ReadCompletedBackup(string root, string receipt, IReadOnlyList<string> protectedPaths)
    {
        try
        {
            if (!File.Exists(receipt) || HasReparseAncestor(receipt) || new FileInfo(receipt).Length > 65536) return null;
            var record = JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(receipt), JsonOptions);
            if (record is null || !ValidMigrationId(record.Id) || !IsSha256(record.ShimSha256)
                || !IsSha256(record.PackageSha256)
                || (record.ManifestSha256 is not null && !IsSha256(record.ManifestSha256))) return null;
            var backupRoot = Path.Combine(root, "MigrationBackup");
            var entry = Path.Combine(backupRoot, "AsterLauncher-" + record.Id + ".exe");
            var app = Path.Combine(backupRoot, "App-" + record.Id);
            var tools = Path.Combine(backupRoot, "MigrationTools-" + record.Id);
            var manifestPath = Path.Combine(backupRoot, "release-files-" + record.Id + ".json");
            var stage = Path.Combine(root, ".aster-migration-" + record.Id);
            if (!string.Equals(receipt, Path.Combine(backupRoot, "migration-" + record.Id + ".json"), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.OldExeBackup, entry, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.OldAppBackup, app, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.OldToolsBackup, tools, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.Stage, stage, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(entry) || Directory.Exists(entry)
                || Directory.Exists(app) != record.HadApp || File.Exists(app)
                || Directory.Exists(tools) != record.HadTools || File.Exists(tools)
                || File.Exists(manifestPath) != record.HadManifest || Directory.Exists(manifestPath)) return null;
            foreach (var path in new[] { receipt, entry, app, tools, manifestPath, stage })
            {
                if (HasReparseAncestor(path)
                    || protectedPaths.Any(data => PathsOverlap(path, data))) return null;
            }
            if (File.Exists(stage) || (Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any())) return null;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (record.HadApp || record.HadTools)
            {
                // A manifest proves the owned file set. Older manifest-less trees
                // can contain user files, so leave those for manual inspection.
                if (!record.HadManifest || new FileInfo(manifestPath).Length > 16 * 1024 * 1024) return null;
                var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Version)
                    || manifest.Files is null || manifest.Files.Count is < 1 or > 10000
                    || !manifest.Files.ContainsKey(EntryName)
                    || (record.HadApp && !manifest.Files.ContainsKey("App/AsterLauncher.exe"))
                    || (record.HadTools && !manifest.Files.ContainsKey("MigrationTools/AsterLauncher.Migrator.dll"))) return null;
                foreach (var (name, specification) in manifest.Files)
                {
                    if (!SafeEntry(name) || specification is null || specification.Length < 0 || !IsSha256(specification.Sha256)) return null;
                    string path;
                    if (name == EntryName) path = entry;
                    else if (record.HadApp && name.StartsWith("App/", StringComparison.Ordinal))
                        path = Path.Combine(app, name[4..].Replace('/', Path.DirectorySeparatorChar));
                    else if (record.HadTools && name.StartsWith("MigrationTools/", StringComparison.Ordinal))
                        path = Path.Combine(tools, name[15..].Replace('/', Path.DirectorySeparatorChar));
                    else return null;
                    path = Path.GetFullPath(path);
                    if (!IsWithin(backupRoot, path) || HasReparseAncestor(path) || !File.Exists(path)
                        || new FileInfo(path).Length != specification.Length
                        || protectedPaths.Any(data => PathsOverlap(path, data))) return null;
                    if (path != entry && !files.Add(path)) return null;
                    var parent = Path.GetDirectoryName(path);
                    while (parent is not null && (PathsEqual(parent, app) || IsWithin(app, parent)
                        || PathsEqual(parent, tools) || IsWithin(tools, parent)))
                    {
                        directories.Add(parent);
                        parent = Path.GetDirectoryName(parent);
                    }
                }
                // Exact membership rejects extra documents, data files and links,
                // without rereading hundreds of MiB merely to clean old programs.
                foreach (var tree in new[] { app, tools }.Where(Directory.Exists))
                {
                    var pending = new Stack<string>();
                    pending.Push(tree);
                    while (pending.Count > 0)
                    {
                        var directory = pending.Pop();
                        if (!directories.Contains(directory) || HasReparseAncestor(directory)) return null;
                        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                        {
                            if (IsReparse(child) || protectedPaths.Any(data => PathsOverlap(child, data))) return null;
                            if (Directory.Exists(child)) pending.Push(child);
                            else if (!files.Contains(child)) return null;
                        }
                    }
                }
            }
            else if (record.HadManifest) return null;
            if (record.HadManifest) files.Add(manifestPath);
            return new CompletedBackup(record.Id, receipt, File.GetLastWriteTimeUtc(receipt), files.ToArray(), directories.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ValidMigrationId(string? id) => id is { Length: 23 } && id[14] == '-'
        && DateTime.TryParseExact(id[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _) && id[15..].All(Uri.IsHexDigit);

    private static bool HasReparseAncestor(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current)) return true;
        return false;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool PathsOverlap(string left, string right) => PathsEqual(left, right) || IsWithin(left, right) || IsWithin(right, left);
    private sealed record CompletedBackup(string Id, string Receipt, DateTime CompletedAtUtc, string[] Files, string[] Directories);

    private static void Recover(string rootArgument)
    {
        var root = Path.GetFullPath(rootArgument);
        if (!Directory.Exists(root) || IsReparse(root)) throw new DirectoryNotFoundException("迁移目录不存在或是目录链接。");
        var journal = Path.Combine(root, ".aster-migration.json");
        if (!File.Exists(journal) || IsReparse(journal)) throw new FileNotFoundException("没有待恢复的迁移记录。", journal);
        EnsureNotRunning(Path.Combine(root, EntryName), Path.Combine(root, AppName, EntryName));
        var record = JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(journal), JsonOptions)
            ?? throw new InvalidDataException("迁移记录无效。");
        if (record.Id.Length != 23 || record.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException("迁移记录 ID 无效。");
        var backupRoot = Path.Combine(root, "MigrationBackup");
        var stage = Path.Combine(root, ".aster-migration-" + record.Id);
        var backupEntry = Path.Combine(backupRoot, "AsterLauncher-" + record.Id + ".exe");
        var backupApp = Path.Combine(backupRoot, "App-" + record.Id);
        var backupTools = Path.Combine(backupRoot, "MigrationTools-" + record.Id);
        if (!record.OldExeBackup.Equals(backupEntry, StringComparison.OrdinalIgnoreCase)
            || !record.OldAppBackup.Equals(backupApp, StringComparison.OrdinalIgnoreCase)
            || !record.OldToolsBackup.Equals(backupTools, StringComparison.OrdinalIgnoreCase)
            || !record.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)
            || !IsSha256(record.ShimSha256))
            throw new InvalidDataException("迁移记录路径无效。");
        var entry = Path.Combine(root, EntryName);
        var app = Path.Combine(root, AppName);
        var tools = Path.Combine(root, ToolsName);
        var manifest = Path.Combine(root, ManifestName);
        var backupManifest = Path.Combine(backupRoot, "release-files-" + record.Id + ".json");
        if (record.ManifestSha256 is not null)
        {
            if (!IsSha256(record.ManifestSha256)
                || (File.Exists(manifest) && IsReparse(manifest))
                || (File.Exists(backupManifest) && IsReparse(backupManifest)))
                throw new InvalidDataException("恢复清单路径或哈希无效。");
            if (File.Exists(manifest) && (File.Exists(backupManifest) || !record.HadManifest)
                && !HashFile(manifest).Equals(record.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("发布清单已被修改，拒绝自动覆盖。");
        }
        foreach (var path in new[] { entry, app, tools, backupEntry, backupApp, backupTools, stage })
            if ((File.Exists(path) || Directory.Exists(path)) && IsReparse(path))
                throw new InvalidOperationException("恢复路径包含链接，拒绝操作：" + path);
        if (File.Exists(backupEntry))
        {
            if (File.Exists(entry))
            {
                if (!HashFile(entry).Equals(record.ShimSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("顶层 EXE 已被修改，拒绝自动覆盖。");
                Directory.CreateDirectory(stage);
                File.Move(entry, Path.Combine(stage, "RecoveredShim.exe"));
            }
            File.Move(backupEntry, entry);
        }
        RecoverDirectory(app, backupApp, Path.Combine(stage, AppName), record.HadApp, stage, "RecoveredApp");
        RecoverDirectory(tools, backupTools, Path.Combine(stage, ToolsName), record.HadTools, stage, "RecoveredTools");
        if (record.ManifestSha256 is not null && (File.Exists(backupManifest)
            || (!record.HadManifest && !File.Exists(Path.Combine(stage, ManifestName)))))
        {
            if (File.Exists(manifest))
            {
                Directory.CreateDirectory(stage);
                File.Move(manifest, Path.Combine(stage, "RecoveredManifest.json"));
            }
            if (File.Exists(backupManifest)) File.Move(backupManifest, manifest);
        }
        File.Delete(journal);
        File.WriteAllText(Path.Combine(root, "migration-recovery.log"), "已恢复旧版启动入口；暂存的新文件保留在 " + stage);
    }

    private static void RecoverDirectory(string current, string backup, string uninstalledStage,
        bool hadOriginal, string stage, string recoveredName)
    {
        if (Directory.Exists(backup))
        {
            if (Directory.Exists(current))
            {
                Directory.CreateDirectory(stage);
                Directory.Move(current, Path.Combine(stage, recoveredName));
            }
            Directory.Move(backup, current);
        }
        else if (!hadOriginal && !Directory.Exists(uninstalledStage) && Directory.Exists(current))
        {
            Directory.CreateDirectory(stage);
            Directory.Move(current, Path.Combine(stage, recoveredName));
        }
    }

    private static void ExtractAndVerify(string package, string stage)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("发布包文件数异常。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!SafeEntry(name) || (entry.ExternalAttributes & 0x400) != 0 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("发布包路径或链接无效：" + name);
            if (name.EndsWith('/')) continue;
            if (entry.Length < 0 || entry.Length > 1_073_741_824)
                throw new InvalidDataException("发布包文件过大：" + name);
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("发布包存在重复文件：" + name);
        }
        if (!entries.TryGetValue("release-files.json", out var manifestEntry)) throw new InvalidDataException("发布包缺少 release-files.json。");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestStream, JsonOptions)
            ?? throw new InvalidDataException("发布包文件清单无效。");
        if (manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null
            || manifest.Files.Count == 0 || manifest.Files.Count != entries.Count - 1)
            throw new InvalidDataException("发布包文件清单不完整。");
        if (!manifest.Files.ContainsKey("App/AsterLauncher.exe") || !manifest.Files.ContainsKey("AsterLauncher.exe")
            || !manifest.Files.ContainsKey("MigrationTools/AsterLauncher.Migrator.dll"))
            throw new InvalidDataException("发布包缺少入口、应用或迁移工具。");
        var requiredBytes = entries.Values.Sum(entry => entry.Length);
        if (requiredBytes > 4L * 1024 * 1024 * 1024)
            throw new InvalidDataException("发布包解压总量超过 4 GiB 限制。");
        if (new DriveInfo(Path.GetPathRoot(stage)!).AvailableFreeSpace < requiredBytes + 64L * 1024 * 1024)
            throw new IOException("安装卷空间不足，无法完整暂存发布包。");
        manifestEntry.ExtractToFile(Path.Combine(stage, ManifestName));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, specification) in manifest.Files)
        {
            if (!(name.StartsWith("App/", StringComparison.Ordinal)
                  || name.StartsWith("MigrationTools/", StringComparison.Ordinal)
                  || name == EntryName) || !SafeEntry(name) || !seen.Add(name)
                || !entries.TryGetValue(name, out var entry) || specification is null
                || !IsSha256(specification.Sha256) || specification.Length != entry.Length)
                throw new InvalidDataException("发布包文件清单不匹配：" + name);
            var target = Path.GetFullPath(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(stage, target)) throw new InvalidDataException("发布包路径越界：" + name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!HashFile(target).Equals(specification.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("解压后文件校验失败：" + name);
        }
    }

    private static bool SafeEntry(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Contains('\0')) return false;
        var components = name.TrimEnd('/').Split('/');
        return components.All(component => component.Length > 0 && component != "." && component != ".."
            && !component.EndsWith(' ') && !component.EndsWith('.')
            && !"CON PRN AUX NUL COM1 COM2 COM3 COM4 COM5 COM6 COM7 COM8 COM9 LPT1 LPT2 LPT3 LPT4 LPT5 LPT6 LPT7 LPT8 LPT9"
                .Split(' ').Contains(Path.GetFileNameWithoutExtension(component).ToUpperInvariant()));
    }

    private static void EnsureNotRunning(params string[] executables)
    {
        foreach (var process in Process.GetProcessesByName("AsterLauncher"))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && executables.Any(executable =>
                            Path.GetFullPath(process.MainModule?.FileName ?? "")
                                .Equals(executable, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("请先退出此安装目录中正在运行的 AsterLauncher，再迁移。");
                }
                catch (System.ComponentModel.Win32Exception) { /* Another user's process is not this install. */ }
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsWithin(string parent, string candidate) =>
        Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    private sealed record ReleaseManifest(int Format, string Version, Dictionary<string, FileSpecification> Files);
    private sealed record FileSpecification(long Length, string Sha256);
    private sealed record MigrationRecord(string Id, string OldExeBackup, string OldAppBackup, string OldToolsBackup, string Stage,
        string ShimSha256, string PackageSha256, bool HadApp, bool HadTools, string? ManifestSha256 = null, bool HadManifest = false);
}
