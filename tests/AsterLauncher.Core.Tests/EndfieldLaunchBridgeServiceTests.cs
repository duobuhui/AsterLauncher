using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldLaunchBridgeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "endfield-bridge-service-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _ownedFixtures = [];
    private string Game => Path.Combine(_root, "game", "Endfield.exe");
    private string Parent => Path.Combine(_root, "launcher", "Games.exe");
    private string Script => Path.Combine(_root, "Services", "StartEndfieldBridge.ps1");
    private string Data => Path.Combine(_root, "Data");

    public EndfieldLaunchBridgeServiceTests()
    {
        foreach (var path in new[] { Game, Parent, Script })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, path.EndsWith(".ps1", StringComparison.Ordinal) ? "# owned fixture" : "MZ");
        }
    }

    [Fact]
    public async Task StartedResultUsesStdinAndPrivateEnvironmentAndLocksSignedFileUntilAcknowledged()
    {
        ProcessStartInfo? captured = null;
        var service = Service(info =>
        {
            captured = info;
            Assert.Throws<IOException>(() => File.Move(Parent, Parent + ".old"));
            return StartFixture(info, "started", validateArguments: true);
        });
        var result = await service.LaunchAsync(Game, Parent, null, ["value with spaces", "中文\"参数", "tail\\"], Data, Script);
        Assert.Equal(new EndfieldBridgeLaunchResult(101, 202, false), result);
        Assert.NotNull(captured);
        Assert.True(captured.CreateNoWindow);
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.RedirectStandardInput);
        Assert.Equal(Path.Combine(Data, "launch", "temp"), captured.WorkingDirectory);
        Assert.Equal(captured.WorkingDirectory, captured.Environment["TEMP"]);
        Assert.Equal(captured.WorkingDirectory, captured.Environment["TMP"]);
        Assert.StartsWith(captured.WorkingDirectory, captured.Environment["PSModuleAnalysisCachePath"]);
        Assert.DoesNotContain(Game, captured.ArgumentList);
        Assert.DoesNotContain("中文\"参数", captured.ArgumentList);
        Assert.Equal(Script, captured.ArgumentList.Last());
        using var unlocked = new FileStream(Parent, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact]
    public async Task CompletedAcknowledgementIsFinalAndNeverStartsAgain()
    {
        var calls = 0;
        var service = Service(info => { calls++; return StartFixture(info, "completed"); });
        var result = await service.LaunchAsync(Game, Parent, null, [], Data, Script);
        Assert.True(result.HasExited);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("wrong-request")]
    [InlineData("invalid-pid")]
    [InlineData("oversized")]
    public async Task BadAcknowledgementDoesNotRetryAndDoesNotExposeInput(string mode)
    {
        var calls = 0;
        var service = Service(info => { calls++; return StartFixture(info, mode); });
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(Game, Parent, null, ["secret-fixture-value"], Data, Script));
        Assert.DoesNotContain("secret-fixture-value", exception.Message);
        Assert.DoesNotContain(_root, exception.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TimeoutLeavesKeeperAliveAndNeverRetriesOrKillsIt()
    {
        var calls = 0;
        var service = Service(info => { calls++; return StartFixture(info, "no-result"); }, TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(Game, Parent, null, [], Data, Script));
        Assert.False(Assert.Single(_ownedFixtures).HasExited);
        var signal = Assert.Single(Directory.EnumerateFiles(Path.Combine(Data, "launch"), "cancel.request", SearchOption.AllDirectories));
        Assert.Equal(0, new FileInfo(signal).Length);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationAfterKeeperStartsLeavesItAlive()
    {
        using var cancellation = new CancellationTokenSource();
        var service = Service(info =>
        {
            var process = StartFixture(info, "no-result");
            cancellation.CancelAfter(300);
            return process;
        });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(Game, Parent, null, [], Data, Script, cancellation.Token));
        Assert.False(Assert.Single(_ownedFixtures).HasExited);
        var signal = Assert.Single(Directory.EnumerateFiles(Path.Combine(Data, "launch"), "cancel.request", SearchOption.AllDirectories));
        Assert.Equal(0, new FileInfo(signal).Length);
    }

    [Fact]
    public async Task UnverifiedPublisherNeverStartsKeeper()
    {
        var calls = 0;
        var trust = new EndfieldLauncherTrust(_ => new(true, "Unknown Publisher", "subject", new string('A', 64), 0));
        var service = new EndfieldLaunchBridgeService(trust, _ => { calls++; return null; }, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(Game, Parent, null, [], Data, Script));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OversizedUtf8ArgumentsAreRejectedBeforeKeeperStarts()
    {
        var calls = 0;
        var service = Service(_ => { calls++; return null; });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(Game, Parent, null,
            Enumerable.Repeat(new string('界', 4000), 4).ToArray(), Data, Script));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("Other.exe")]
    [InlineData("Endfield.exe:payload.exe")]
    [InlineData("Endfield.dll")]
    public async Task UnexpectedGamePathNeverStartsKeeper(string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(Game)!, name);
        var calls = 0;
        var service = Service(_ => { calls++; return null; });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LaunchAsync(path, Parent, null, [], Data, Script));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CleanupDeletesOnlyInactiveCompletedKnownSessionContents()
    {
        var launchRoot = Path.Combine(Data, "launch");
        string Receipt(string status)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(launchRoot, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { schemaVersion = 1, requestId = id, status }));
            return directory;
        }
        var complete = Receipt("completed");
        var active = Receipt("completed");
        var started = Receipt("started");
        var unknown = Receipt("completed");
        File.WriteAllText(Path.Combine(unknown, "user-file.txt"), "preserve");
        using var lease = new FileStream(Path.Combine(active, "active.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        EndfieldLaunchBridgeService.CleanCompletedSessions(launchRoot);
        Assert.False(Directory.Exists(complete));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(started));
        Assert.True(File.Exists(Path.Combine(unknown, "user-file.txt")));
    }

    [Theory]
    [InlineData("warning", "game_job_isolation_unavailable")]
    [InlineData("unknown-warning", null)]
    public async Task SuccessfulAcknowledgementExposesOnlyFixedWarningCodes(string mode, string? expected)
    {
        var result = await Service(info => StartFixture(info, mode)).LaunchAsync(Game, Parent, null, [], Data, Script);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Theory]
    [InlineData("failed-signature", "signature_untrusted")]
    [InlineData("failed", "bridge_failed")]
    public async Task FailedAcknowledgementPreservesOnlyRecognizedFixedCodes(string mode, string expected)
    {
        var service = Service(info => StartFixture(info, mode));
        var exception = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() => service.LaunchAsync(Game, Parent, null, [], Data, Script));
        Assert.Equal(expected, exception.Code);
        Assert.DoesNotContain("fixture_failure", exception.Message);
        Assert.DoesNotContain(_root, exception.Message);
    }

    private EndfieldLaunchBridgeService Service(Func<ProcessStartInfo, Process?> start, TimeSpan? timeout = null) =>
        new(new EndfieldLauncherTrust(_ => new(true, "Shanghai Hypergryph Network Technology Co., Ltd.", "subject", new string('A', 64), 0)),
            start, timeout ?? TimeSpan.FromSeconds(5));

    private Process StartFixture(ProcessStartInfo captured, string mode, bool validateArguments = false)
    {
        var runtime = Path.Combine(RepositoryRoot(), ".artifacts", "vendor", "node-v24.19.0", "node.exe");
        var code = "const fs=require('fs'),p=require('path');let b='';process.stdin.setEncoding('utf8');"
            + "process.stdin.on('data',x=>b+=x);process.stdin.on('end',()=>{let r=JSON.parse(b);"
            + "let mode=" + JsonSerializer.Serialize(mode) + ";let validate=" + (validateArguments ? "true" : "false") + ";"
            + "if(r.schemaVersion!==1||!/^[a-f0-9]{32}$/.test(r.requestId)||!/^[a-f0-9]{64}$/.test(r.expectedFileSha256))mode='failed';"
            + "if(validate&&JSON.stringify(r.arguments)!==JSON.stringify(['value with spaces','中文\"参数','tail\\\\']))mode='failed';"
            + "if(mode==='no-result'){setTimeout(()=>{},30000);return;}"
            + "let x={schemaVersion:1,requestId:mode==='wrong-request'?'bad':r.requestId,status:mode==='completed'?'completed':mode.startsWith('failed')?'failed':'started',"
            + "errorCode:mode==='failed-signature'?'signature_untrusted':mode==='failed'?'fixture_failure':mode==='warning'?'game_job_isolation_unavailable':mode==='unknown-warning'?'arbitrary-private-value':'',gamePid:mode==='invalid-pid'?0:101,parentPid:202};"
            + "let d=p.join(r.dataRoot,'launch',r.requestId);fs.mkdirSync(d,{recursive:true});"
            + "fs.writeFileSync(p.join(d,'result.json'),mode==='oversized'?' '.repeat(5000):JSON.stringify(x));setTimeout(()=>{},1200);});";
        var info = new ProcessStartInfo(runtime)
        {
            WorkingDirectory = captured.WorkingDirectory, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false), UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var variable in new[] { "TEMP", "TMP", "PSModuleAnalysisCachePath" }) info.Environment[variable] = captured.Environment[variable];
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(code);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Owned keeper fixture failed.");
        _ownedFixtures.Add(Process.GetProcessById(process.Id));
        return process;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AsterLauncher.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository fixture root is unavailable.");
    }
    public void Dispose()
    {
        foreach (var process in _ownedFixtures)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            finally { process.Dispose(); }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
