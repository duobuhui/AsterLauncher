using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AsterLauncher.Infrastructure;

public sealed record EndfieldLauncherTrustResult(string ExecutablePath, bool IsSignatureValid,
    string? Publisher, string? CertificateSubject, string? CertificateSha256, int NativeStatus, string Message)
{
    public bool IsRecognizedPublisher => IsSignatureValid && EndfieldLauncherTrust.IsHypergryphPublisher(Publisher);
    public bool CanUseAsBridgeParent => IsRecognizedPublisher
        && string.Equals(Path.GetFileName(ExecutablePath), "Games.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads local Authenticode evidence without network retrieval or launching the executable.</summary>
public sealed class EndfieldLauncherTrust
{
    private readonly Func<string, SignatureEvidence> _verify;
    public EndfieldLauncherTrust() : this(VerifyNative) { }
    internal EndfieldLauncherTrust(Func<string, SignatureEvidence> verify) => _verify = verify;

    public EndfieldLauncherTrustResult Verify(string executablePath)
    {
        if (!TryNormalizeExecutable(executablePath, out var path) || !File.Exists(path))
            return new(executablePath, false, null, null, null, unchecked((int)0x80070002), "文件不存在或不是本地 EXE。");
        try
        {
            // Keep the same file immutable until its verified signer has been read.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var evidence = _verify(path);
            var recognized = evidence.IsValid && IsHypergryphPublisher(evidence.Publisher);
            return new(path, evidence.IsValid, evidence.Publisher, evidence.Subject, evidence.CertificateSha256,
                evidence.NativeStatus, recognized ? "已验证鹰角数字签名。"
                    : evidence.IsValid ? "签名有效，发布者未识别；仅可由用户明确选择用于接管。"
                    : "未能验证数字签名；不能用于实验性启动桥。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(path, false, null, null, null, exception.HResult, "无法读取或验证数字签名；不能用于实验性启动桥。");
        }
    }

    public Task<EndfieldLauncherTrustResult> VerifyAsync(string executablePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Verify(executablePath), cancellationToken);

    internal static bool TryNormalizeExecutable(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        try
        {
            var normalized = Path.GetFullPath(value);
            if (!Path.IsPathFullyQualified(value) || normalized.StartsWith(@"\\", StringComparison.Ordinal)
                || normalized.Length < 3 || normalized[1] != ':' || normalized[2..].Contains(':')
                || !string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            path = normalized;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsHypergryphPublisher(string? publisher) => publisher is not null
        && (publisher.Equals("上海鹰角网络科技有限公司", StringComparison.Ordinal)
            || publisher.Equals("Shanghai Hypergryph Network Technology Co., Ltd.", StringComparison.OrdinalIgnoreCase));

    internal sealed record SignatureEvidence(bool IsValid, string? Publisher, string? Subject, string? CertificateSha256, int NativeStatus);

    private static SignatureEvidence VerifyNative(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(false, null, null, null, unchecked((int)0x80004001));
        var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(info, pointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
            FileInfo = pointer, StateAction = 1,
            // Cache-only prevents trust-provider network downloads into global certificate caches.
            ProviderFlags = 0x1000 | 0x2000
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) return new(false, null, null, null, status);
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return new(false, null, null, null, unchecked((int)0x800B0100));
            var context = Marshal.PtrToStructure<ProviderCertificatePrefix>(certificate).Certificate;
            if (context == IntPtr.Zero) return new(false, null, null, null, unchecked((int)0x800B0100));
            var encoded = Marshal.PtrToStructure<CertificateContext>(context);
            if (encoded.Encoded == IntPtr.Zero || encoded.EncodedSize is 0 or > 65536)
                return new(false, null, null, null, unchecked((int)0x800B0100));
            var bytes = new byte[encoded.EncodedSize];
            Marshal.Copy(encoded.Encoded, bytes, 0, bytes.Length);
            using var x509 = X509CertificateLoader.LoadCertificate(bytes);
            return new(true, ReadOrganization(context), x509.Subject, x509.GetCertHashString(HashAlgorithmName.SHA256), 0);
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string? ReadOrganization(IntPtr certificate)
    {
        var oid = Marshal.StringToHGlobalAnsi("2.5.4.10");
        try
        {
            var length = CertGetNameString(certificate, 3, 0, oid, null, 0);
            if (length is <= 1 or > 4096) return null;
            var value = new StringBuilder((int)length);
            return CertGetNameString(certificate, 3, 0, oid, value, length) > 1 ? value.ToString() : null;
        }
        finally { Marshal.FreeHGlobal(oid); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificatePrefix { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint Encoding;
        public IntPtr Encoded;
        public uint EncodedSize;
        public IntPtr CertificateInfo;
        public IntPtr Store;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CertGetNameString(IntPtr certificate, uint type, uint flags, IntPtr parameter, StringBuilder? name, uint size);
}
