using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldLaunchBridgeElevationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "endfield-bridge-elevation-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _owned = [];
    private string Game => Path.Combine(_root, "game", "Endfield.exe");
    private string Parent => Path.Combine(_root, "launcher", "Games.exe");
    private string Script => Path.Combine(_root, "Services", "StartEndfieldBridge.ps1");
    private string Data => Path.Combine(_root, "Data");
    private string Temporary => Path.Combine(Data, "launch", "temp");

    public EndfieldLaunchBridgeElevationTests()
    {
        foreach (var path in new[] { Game, Parent, Script })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, path.EndsWith(".ps1", StringComparison.Ordinal) ? "# owned fixture" : "MZ");
        }
    }

    [Fact]
    public void RequestPipeAclUsesActualUserSidAndNoInheritedOrBroadGrants()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        using var pipe = EndfieldLaunchBridgeService.CreateRequestPipe("AsterLauncher.EndfieldBridge." + Guid.NewGuid().ToString("N"));
        var security = pipe.GetAccessControl();
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal(user, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
        Assert.False(rule.IsInherited);
        Assert.NotEqual(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rule.IdentityReference);
        Assert.NotEqual(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rule.IdentityReference);
        Assert.True(pipe.IsAsync);
        Assert.True(pipe.CanWrite);
        Assert.False(pipe.CanRead);
    }
    [Fact]
    public async Task ExplicitRouteUsesAuthenticatedPipeAndKeepsUtf8GameArgumentsOffCommandLine()
    {
        ProcessStartInfo? captured = null;
        var calls = 0;
        var service = Service(info =>
        {
            captured = info;
            calls++;
            Assert.Throws<IOException>(() => File.Move(Parent, Parent + ".old"));
            return Fixture(info, validateArguments: true);
        });
        var result = await service.LaunchElevatedAsync(Game, Parent, null, ["value with spaces", "中文\"参数", "tail\\"], Data, Script);
        Assert.Equal(new EndfieldBridgeLaunchResult(101, 202, false), result);
        Assert.Equal(1, calls);
        Assert.NotNull(captured);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.False(captured.RedirectStandardInput);
        Assert.Null(captured.StandardInputEncoding);
        Assert.Empty(captured.ArgumentList);
        var arguments = ArgumentTokenizer.Parse(captured.Arguments).ToList();
        Assert.Equal(Script, arguments[arguments.IndexOf("-File") + 1]);
        Assert.Matches("^AsterLauncher[.]EndfieldBridge[.][a-f0-9]{32}$", arguments[arguments.IndexOf("-RequestPipe") + 1]);
        Assert.Equal(Temporary, arguments[arguments.IndexOf("-IpcTemporaryDirectory") + 1]);
        Assert.DoesNotContain(Game, captured.Arguments);
        Assert.DoesNotContain(Parent, captured.Arguments);
        Assert.DoesNotContain("中文", captured.Arguments);
        using var unlocked = new FileStream(Parent, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact]
    public async Task UnexpectedClientPidReceivesNoPayloadAndNeverRetries()
    {
        var marker = Path.Combine(_root, "wrong-peer-byte-count.txt");
        var calls = 0;
        var service = Service(info =>
        {
            calls++;
            Fixture(info, marker: marker);
            return Idle(); // The pipe client is test-owned, but is not this returned keeper.
        });
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchElevatedAsync(Game, Parent, null, ["private-fixture-argument"], Data, Script));
        Assert.Equal("ipc_peer_mismatch", error.Code);
        Assert.False(error.RequiresElevation);
        Assert.Equal(1, calls);
        for (var attempt = 0; attempt < 40 && !File.Exists(marker); attempt++) await Task.Delay(50);
        Assert.True(File.Exists(marker));
        Assert.Equal("0", File.ReadAllText(marker));
        Assert.All(_owned, process => Assert.False(process.HasExited));
        Assert.Single(Directory.EnumerateFiles(Data, "cancel.request", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UacCancellationReturnsFixedCodeWithoutRetry()
    {
        var calls = 0;
        var service = Service(info =>
        {
            calls++;
            Assert.True(info.UseShellExecute);
            Assert.Equal("runas", info.Verb);
            throw new System.ComponentModel.Win32Exception(1223, "private-fixture-argument");
        });
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchElevatedAsync(Game, Parent, null, ["private-fixture-argument"], Data, Script));
        Assert.Equal("elevation_cancelled", error.Code);
        Assert.False(error.RequiresElevation);
        Assert.DoesNotContain("private-fixture-argument", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InitialTimeoutExcludesSimulatedHumanUacDecision()
    {
        var service = Service(info =>
        {
            Thread.Sleep(1100); // Injected test start; never invokes real ShellExecute/UAC.
            return Fixture(info);
        }, TimeSpan.FromMilliseconds(900));
        var result = await service.LaunchElevatedAsync(Game, Parent, null, [], Data, Script);
        Assert.False(result.HasExited);
    }

    [Fact]
    public async Task UnconnectedPipeTimeoutLeavesKeeperAliveAndSignalsCancellation()
    {
        var calls = 0;
        var service = Service(_ => { calls++; return Idle(); }, TimeSpan.FromMilliseconds(400));
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchElevatedAsync(Game, Parent, null, [], Data, Script));
        Assert.Equal("wait_timeout", error.Code);
        Assert.Equal(1, calls);
        Assert.False(Assert.Single(_owned).HasExited);
        Assert.Single(Directory.EnumerateFiles(Data, "cancel.request", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelledPipeWaitLeavesKeeperAliveAndSignalsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = Service(_ => { var process = Idle(); cancellation.CancelAfter(200); return process; });
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchElevatedAsync(Game, Parent, null, [], Data, Script, cancellation.Token));
        Assert.Equal("wait_cancelled", error.Code);
        Assert.False(Assert.Single(_owned).HasExited);
        Assert.Single(Directory.EnumerateFiles(Data, "cancel.request", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NullShellProcessIsRejectedWithoutRetryOrPayload()
    {
        var calls = 0;
        var service = Service(_ => { calls++; return null; });
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchElevatedAsync(Game, Parent, null, ["private-fixture-argument"], Data, Script));
        Assert.False(error.RequiresElevation);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.EnumerateFiles(Data, "result.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("{\"nativeError\":740,\"nativeStage\":\"create_process\"}", true, 740, "create_process")]
    [InlineData("{\"nativeError\":740,\"nativeStage\":\"initialize_attribute_list\"}", false, 740, "initialize_attribute_list")]
    [InlineData("{\"nativeError\":5,\"nativeStage\":\"create_process\"}", false, 5, "create_process")]
    [InlineData("{}", false, null, null)]
    [InlineData("{\"nativeError\":-1,\"nativeStage\":\"create_process\"}", false, null, "create_process")]
    [InlineData("{\"nativeError\":65536,\"nativeStage\":\"create_process\"}", false, null, "create_process")]
    [InlineData("{\"nativeError\":\"740\",\"nativeStage\":\"create_process\"}", false, null, "create_process")]
    [InlineData("{\"nativeError\":740.5,\"nativeStage\":\"create_process\"}", false, null, "create_process")]
    [InlineData("{\"nativeError\":740,\"nativeStage\":\"private-fixture-value\"}", false, 740, null)]
    [InlineData("{\"nativeError\":null,\"nativeStage\":false}", false, null, null)]
    public async Task NativeFieldsAreBoundedAndOnlyExactOfficial740AllowsConsent(string fields, bool consent, int? nativeError, string? nativeStage)
    {
        var calls = 0;
        var service = Service(info => { calls++; Assert.False(info.UseShellExecute); return Fixture(info, "failed", "official_process_creation_failed", fields); });
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() =>
            service.LaunchAsync(Game, Parent, null, ["private-fixture-argument"], Data, Script));
        Assert.Equal("official_process_creation_failed", error.Code);
        Assert.Equal(nativeError, error.NativeError);
        Assert.Equal(nativeStage, error.NativeStage);
        Assert.Equal(consent, error.RequiresElevation);
        Assert.DoesNotContain("private-fixture", error.Message);
        Assert.Equal(1, calls); // Ordinary launch never promotes itself.
    }

    [Fact]
    public async Task GameProcess740DoesNotAllowOfficialParentRetry()
    {
        var service = Service(info => Fixture(info, "failed", "game_process_creation_failed", "{\"nativeError\":740,\"nativeStage\":\"create_process\"}"));
        var error = await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() => service.LaunchAsync(Game, Parent, null, [], Data, Script));
        Assert.Equal(740, error.NativeError);
        Assert.False(error.RequiresElevation);
    }

    [Fact]
    public async Task SuccessReceiptRetainsOnlyValidatedNativeFields()
    {
        var result = await Service(info => Fixture(info, nativeFields: "{\"nativeError\":5,\"nativeStage\":\"update_attribute_list\"}"))
            .LaunchAsync(Game, Parent, null, [], Data, Script);
        Assert.Equal(5, result.NativeError);
        Assert.Equal("update_attribute_list", result.NativeStage);
    }

    private EndfieldLaunchBridgeService Service(Func<ProcessStartInfo, Process?> start, TimeSpan? timeout = null) =>
        new(new EndfieldLauncherTrust(_ => new(true, "Shanghai Hypergryph Network Technology Co., Ltd.", "subject", new string('A', 64), 0)),
            start, timeout ?? TimeSpan.FromSeconds(5));

    private Process Fixture(ProcessStartInfo captured, string status = "started", string code = "", string nativeFields = "{}", string? marker = null, bool validateArguments = false)
    {
        var readRequest = captured.UseShellExecute ? ReadPipe(captured) : "let chunks=[];process.stdin.on('data',b=>chunks.push(b));process.stdin.on('end',()=>finish(Buffer.concat(chunks)));";
        var script = "const fs=require('fs'),p=require('path');function finish(bytes){"
            + "let marker=" + JsonSerializer.Serialize(marker) + ";if(marker){fs.writeFileSync(marker,String(bytes.length));setTimeout(()=>{},30000);return;}"
            + "let r=JSON.parse(bytes.toString('utf8'));let validate=" + (validateArguments ? "true" : "false") + ";"
            + "if(validate&&JSON.stringify(r.arguments)!==JSON.stringify(['value with spaces','中文\"参数','tail\\\\']))throw Error('fixture arguments differ');"
            + "let x={schemaVersion:1,requestId:r.requestId,status:" + JsonSerializer.Serialize(status) + ",errorCode:" + JsonSerializer.Serialize(code) + ",gamePid:101,parentPid:202};"
            + "Object.assign(x,JSON.parse(" + JsonSerializer.Serialize(nativeFields) + "));"
            + "let d=p.join(r.dataRoot,'launch',r.requestId);fs.mkdirSync(d,{recursive:true});fs.writeFileSync(p.join(d,'result.json'),JSON.stringify(x));setTimeout(()=>{},1200);}"
            + readRequest;
        return Node(script, !captured.UseShellExecute);
    }

    private static string ReadPipe(ProcessStartInfo captured)
    {
        var arguments = ArgumentTokenizer.Parse(captured.Arguments).ToList();
        var pipeName = arguments[arguments.IndexOf("-RequestPipe") + 1];
        return "const pipe=" + JsonSerializer.Serialize(@"\\.\pipe\" + pipeName) + ";"
            + "function connect(){let fd;try{fd=fs.openSync(pipe,'r');}catch(e){if(e.code==='ENOENT'||e.code==='EBUSY'){setTimeout(connect,20);return;}throw e;}"
            + "let chunks=[],buf=Buffer.alloc(4096);try{for(;;){let n=fs.readSync(fd,buf,0,buf.length,null);if(!n)break;chunks.push(Buffer.from(buf.subarray(0,n)));}}catch(e){if(e.code!=='EPIPE')throw e;}finally{fs.closeSync(fd);}finish(Buffer.concat(chunks));}connect();";
    }

    private Process Idle() => Node("setTimeout(()=>{},30000)", false);
    private Process Node(string script, bool stdin)
    {
        var runtime = Path.Combine(RepositoryRoot(), ".artifacts", "vendor", "node-v24.19.0", "node.exe");
        var info = new ProcessStartInfo(runtime) { WorkingDirectory = Temporary, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = stdin };
        if (stdin) info.StandardInputEncoding = new UTF8Encoding(false);
        info.Environment["TEMP"] = Temporary;
        info.Environment["TMP"] = Temporary;
        info.Environment["PSModuleAnalysisCachePath"] = Path.Combine(Temporary, "PowerShellModuleAnalysisCache");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Owned pipe fixture unavailable.");
        _owned.Add(Process.GetProcessById(process.Id));
        return process;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AsterLauncher.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository fixture root unavailable.");
    }
    public void Dispose()
    {
        foreach (var process in _owned)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            finally { process.Dispose(); }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}