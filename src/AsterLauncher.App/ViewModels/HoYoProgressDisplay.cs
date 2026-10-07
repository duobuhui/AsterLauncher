using AsterLauncher.Core;

namespace AsterLauncher.App.ViewModels;

/// <summary>Presentation state for one installation's current maintenance phase.</summary>
internal sealed class HoYoProgressDisplay
{
    private string? _stage;
    private long _lastBytes;
    private long _lastReportedBytes;
    private DateTimeOffset _lastSample;

    public string StageText { get; private set; } = "";
    public string AmountText { get; private set; } = "";
    public string SpeedText { get; private set; } = "";
    public string CurrentFile { get; private set; } = "";
    public string DetailText { get; private set; } = "";
    public double Progress { get; private set; }
    public bool HasTotal { get; private set; }
    public bool IsNetworkTransfer { get; private set; }

    public void Apply(HoYoProgress report, DateTimeOffset now)
    {
        var transferred = report.TransferredBytes ?? report.CompletedBytes;
        var newPhase = _stage != report.Stage || IsNetworkTransfer != report.IsNetworkTransfer
            || transferred < _lastReportedBytes;
        if (newPhase)
        {
            _stage = report.Stage;
            _lastBytes = transferred;
            _lastSample = now;
            SpeedText = "";
        }
        _lastReportedBytes = transferred;
        IsNetworkTransfer = report.IsNetworkTransfer;
        StageText = report.Stage + (report.TotalFiles > 0 ? $" · {report.CompletedFiles:N0}/{report.TotalFiles:N0}" : "");
        CurrentFile = report.CurrentFile ?? "";
        HasTotal = report.TotalBytes > 0 || report.TotalFiles > 0;
        Progress = Math.Clamp(report.TotalBytes > 0 ? 100d * report.CompletedBytes / report.TotalBytes
            : report.TotalFiles > 0 ? 100d * report.CompletedFiles / report.TotalFiles : 0, 0, 100);
        AmountText = report.TotalBytes > 0
            ? $"{(IsNetworkTransfer ? "已下载" : "已处理")} {Bytes(report.CompletedBytes)} / {Bytes(report.TotalBytes)}" : "";
        DetailText = report.Stage switch
        {
            "正在校验另一服可复用资源" => "校验通过后复用另一服资源，耗时取决于磁盘速度。",
            "正在检查本地文件" => "正在读取本地文件；只会下载缺失或损坏的内容。",
            "正在检查下载缓存" => "正在校验已有缓存，确认后继续使用已下载的内容。",
            "正在复用另一服资源" => "正在复用已校验的相同资源，减少下载量和空间占用。",
            _ => ""
        };
        var seconds = (now - _lastSample).TotalSeconds;
        if (IsNetworkTransfer && !newPhase && seconds >= 1)
        {
            SpeedText = $"{Bytes((long)(Math.Max(0, transferred - _lastBytes) / seconds))}/s";
            _lastBytes = transferred;
            _lastSample = now;
        }
        if (!IsNetworkTransfer) SpeedText = "";
    }

    public void Stop() => SpeedText = "";

    public void Reset()
    {
        _stage = null;
        _lastBytes = _lastReportedBytes = 0;
        _lastSample = default;
        StageText = AmountText = SpeedText = CurrentFile = DetailText = "";
        Progress = 0;
        HasTotal = IsNetworkTransfer = false;
    }

    private static string Bytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.00} GiB"
        : value >= 1048576 ? $"{value / 1048576d:0.0} MiB" : value >= 1024 ? $"{value / 1024d:0.0} KiB" : $"{value} B";
}