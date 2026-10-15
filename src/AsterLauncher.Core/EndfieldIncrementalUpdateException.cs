namespace AsterLauncher.Core;

/// <summary>A safe incremental failure never authorizes a fallback to complete-file network downloads.</summary>
public sealed class EndfieldIncrementalUpdateException(Exception? innerException = null) : IOException(
    "增量补丁无法安全应用；已保留缓存并停止更新，未自动下载完整资源。请核对源文件或官方补丁。", innerException);