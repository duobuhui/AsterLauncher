using AsterLauncher.App.ViewModels;
using Xunit;

namespace AsterLauncher.Core.Tests;

public sealed class HoYoProgressDisplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerificationReplacesDownloadImmediatelyAndClearsNetworkSpeed()
    {
        var display = new HoYoProgressDisplay();
        display.Apply(new("正在下载", 0, 3, 0, 10000, IsNetworkTransfer: true), Start);
        display.Apply(new("正在下载", 1, 3, 4096, 10000, IsNetworkTransfer: true), Start.AddSeconds(1));
        Assert.NotEmpty(display.SpeedText);
        display.Apply(new("正在校验另一服可复用资源", 0, 20, 1024, 4096, "data/large.bundle"), Start.AddSeconds(1).AddMilliseconds(1));
        Assert.Empty(display.SpeedText);
        Assert.Equal(25, display.Progress);
        Assert.True(display.HasTotal);
        Assert.Equal("data/large.bundle", display.CurrentFile);
        Assert.Contains("另一服", display.DetailText);
        Assert.StartsWith("正在校验另一服", display.StageText);
        Assert.StartsWith("已处理", display.AmountText);
    }

    [Fact]
    public void CachedBytesAndResumePrefixDoNotCountAsNetworkSpeed()
    {
        var display = new HoYoProgressDisplay();
        display.Apply(new("正在下载", 0, 3, 5000000, 10000000, IsNetworkTransfer: true, TransferredBytes: 0), Start);
        display.Apply(new("正在下载", 1, 3, 8000000, 10000000, IsNetworkTransfer: true, TransferredBytes: 1024), Start.AddSeconds(1));
        Assert.Equal(80, display.Progress);
        Assert.Equal("1.0 KiB/s", display.SpeedText);
        display.Apply(new("正在下载", 2, 3, 9000000, 10000000, IsNetworkTransfer: true, TransferredBytes: 1024), Start.AddSeconds(2));
        Assert.Equal("0 B/s", display.SpeedText);
    }

    [Fact]
    public void NewNetworkPhaseResetsSpeedSamplingAndStopClearsSpeed()
    {
        var display = new HoYoProgressDisplay();
        display.Apply(new("渠道组件", 0, 1, 0, 1000, IsNetworkTransfer: true), Start);
        display.Apply(new("渠道组件", 0, 1, 600, 1000, IsNetworkTransfer: true), Start.AddSeconds(1));
        display.Apply(new("游戏资源", 0, 10, 1000000, 2000000, IsNetworkTransfer: true), Start.AddSeconds(1).AddMilliseconds(1));
        Assert.Empty(display.SpeedText);
        display.Apply(new("游戏资源", 0, 10, 1001024, 2000000, IsNetworkTransfer: true), Start.AddSeconds(2).AddMilliseconds(1));
        Assert.Equal("1.0 KiB/s", display.SpeedText);
        display.Stop();
        Assert.Empty(display.SpeedText);
        Assert.True(display.Progress > 50);
    }

    [Fact]
    public void ResetRemovesPreviousTaskStateAndUnknownTotalIsIndeterminate()
    {
        var display = new HoYoProgressDisplay();
        display.Apply(new("正在校验另一服可复用资源", 20, 20, 4000, 4000, "last.bundle"), Start);
        display.Reset();
        Assert.Empty(display.StageText);
        Assert.Empty(display.CurrentFile);
        Assert.Empty(display.DetailText);
        Assert.Empty(display.AmountText);
        Assert.False(display.HasTotal);
        Assert.Equal(0, display.Progress);
        display.Apply(new("正在读取另一服资源清单", 0, 0, 0, 0), Start.AddSeconds(1));
        Assert.False(display.HasTotal);
    }

    [Fact]
    public void FileCountIsUsableWhenByteCountIsUnavailable()
    {
        var display = new HoYoProgressDisplay();
        display.Apply(new("正在复用另一服资源", 3, 4, 0, 0), Start);
        Assert.True(display.HasTotal);
        Assert.Equal(75, display.Progress);
        Assert.Empty(display.SpeedText);
    }

    [Fact]
    public void ThrottlingAlwaysDeliversPhaseTransitionsAndCompletion()
    {
        var ui = new QueuedContext();
        var previous = SynchronizationContext.Current;
        var received = new List<HoYoProgress>();
        MaintenanceProgress<HoYoProgress> progress;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            progress = new(received.Add, p => p.Stage, p => p.TotalFiles > 0 && p.CompletedFiles == p.TotalFiles);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        progress.Report(new("下载", 0, 10, 0, 100));
        progress.Report(new("校验", 0, 10, 0, 100));
        progress.Report(new("校验", 10, 10, 100, 100));
        ui.Drain();
        Assert.Equal(3, received.Count);
        Assert.Equal("校验", received[1].Stage);
        Assert.Equal(10, received[^1].CompletedFiles);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void Drain()
        {
            while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}