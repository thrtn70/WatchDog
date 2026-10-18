using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace WatchDog.CaptureIsolation.Windows;

internal sealed record WindowsAuthenticodeResult(
    PackagePublisherTrustStatus Status,
    string PublisherCertificateSha256);

internal interface IWindowsAuthenticodeApi
{
    WindowsAuthenticodeResult Verify(SafeFileHandle file, string path);
}

internal sealed record WindowsTrustProviderState(int Status, object NativeState);

internal interface IWindowsTrustProviderApi
{
    WindowsTrustProviderState Verify(SafeFileHandle file, string path);
    X509Certificate2 LoadSignerCertificate(WindowsTrustProviderState state);
    bool Close(WindowsTrustProviderState state);
}

/// <summary>
/// Retains an exact read-only package file without delete/write sharing. It emits only a neutral
/// file observation; the managed signed-manifest verifier owns trusted evidence minting.
/// </summary>
internal sealed class WindowsPackageFileLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly IWindowsAuthenticodeApi _authenticode;
    private readonly string _path;
    private bool _disposed;

    private WindowsPackageFileLease(
        FileStream stream,
        IWindowsAuthenticodeApi authenticode,
        string path,
        PackageFileObservation observation)
    {
        _stream = stream;
        _authenticode = authenticode;
        _path = path;
        Observation = observation;
    }

    internal PackageFileObservation Observation { get; }

    internal static WindowsPackageFileLease Open(
        string path,
        CaptureIsolationPayloadRole role,
        string relativePath,
        CancellationToken cancellationToken,
        IWindowsAuthenticodeApi? authenticode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(role) || !IsSafeRelativePath(relativePath))
            throw new HelperBoundaryException("helper.package.path-invalid");
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || IsRemoteOrDevicePath(path) ||
            !File.Exists(fullPath))
        {
            throw new HelperBoundaryException("helper.package.path-invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException or
                System.Security.SecurityException)
        {
            throw new HelperBoundaryException("helper.package.open-failed");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Hash(stream, cancellationToken);
            var trustApi = authenticode ?? WinVerifyTrustAuthenticodeApi.Instance;
            var trust = trustApi.Verify(stream.SafeFileHandle, fullPath);
            var observation = new PackageFileObservation(
                role,
                relativePath,
                stream.Length,
                digest,
                trust.Status,
                trust.PublisherCertificateSha256);
            return new(stream, trustApi, fullPath, observation);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal bool Revalidate(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream.Length != Observation.Length ||
            !string.Equals(Hash(_stream, cancellationToken), Observation.Sha256Digest, StringComparison.Ordinal))
        {
            return false;
        }
        var trust = _authenticode.Verify(_stream.SafeFileHandle, _path);
        return trust.Status == Observation.PublisherTrust &&
            string.Equals(
                trust.PublisherCertificateSha256,
                Observation.PublisherCertificateSha256,
                StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _stream.Dispose();
        _disposed = true;
    }

    private static string Hash(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        stream.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSafeRelativePath(string value) =>
        value.Length is > 0 and <= 240 && !Path.IsPathFullyQualified(value) &&
        !value.Contains('\\') && !value.Contains(':') &&
        value.Split('/').All(static part => part.Length > 0 && part is not "." and not "..");

    private static bool IsRemoteOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith(@"\??\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal);
}

internal sealed class WinVerifyTrustAuthenticodeApi : IWindowsAuthenticodeApi
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertERevoked = unchecked((int)0x800B010C);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CryptERevocationOffline = unchecked((int)0x80092013);
    private readonly IWindowsTrustProviderApi _provider;

    internal static WinVerifyTrustAuthenticodeApi Instance { get; } = new();

    private WinVerifyTrustAuthenticodeApi()
        : this(WindowsTrustProviderApi.Instance)
    {
    }

    internal WinVerifyTrustAuthenticodeApi(IWindowsTrustProviderApi provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public WindowsAuthenticodeResult Verify(SafeFileHandle file, string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        WindowsTrustProviderState? state = null;
        var result = new WindowsAuthenticodeResult(
            PackagePublisherTrustStatus.Unavailable,
            string.Empty);
        var closeSucceeded = true;
        try
        {
            state = _provider.Verify(file, path);
            if (state.Status != 0)
            {
                result = new(MapStatus(state.Status), string.Empty);
            }
            else
            {
                using var certificate = _provider.LoadSignerCertificate(state);
                var hasCodeSigningEku = certificate.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .SelectMany(static extension => extension.EnhancedKeyUsages.Cast<Oid>())
                    .Any(static oid => oid.Value == "1.3.6.1.5.5.7.3.3");
                if (!hasCodeSigningEku)
                {
                    result = new(PackagePublisherTrustStatus.WrongPublisher, string.Empty);
                }
                else
                {
                    result = new(
                        PackagePublisherTrustStatus.Trusted,
                        certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());
                }
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidOperationException or OverflowException)
        {
            result = new(PackagePublisherTrustStatus.Unavailable, string.Empty);
        }
        finally
        {
            if (state is not null)
            {
                try
                {
                    closeSucceeded = _provider.Close(state);
                }
                catch
                {
                    closeSucceeded = false;
                }
            }
        }
        return closeSucceeded
            ? result
            : new(PackagePublisherTrustStatus.Unavailable, string.Empty);
    }

    private static PackagePublisherTrustStatus MapStatus(int status) => status switch
    {
        TrustENoSignature => PackagePublisherTrustStatus.Unsigned,
        TrustEBadDigest => PackagePublisherTrustStatus.InvalidSignature,
        CertEUntrustedRoot => PackagePublisherTrustStatus.ChainUntrusted,
        CertERevoked => PackagePublisherTrustStatus.Revoked,
        CryptERevocationOffline => PackagePublisherTrustStatus.RevocationUnknown,
        CertEExpired => PackagePublisherTrustStatus.Expired,
        _ => PackagePublisherTrustStatus.Unavailable,
    };
}

internal sealed partial class WindowsTrustProviderApi : IWindowsTrustProviderApi
{
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x00000004;
    private const uint RevocationCheckChainExcludeRoot = 0x00000080;
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static WindowsTrustProviderApi Instance { get; } = new();

    private WindowsTrustProviderApi()
    {
    }

    public WindowsTrustProviderState Verify(SafeFileHandle file, string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = nint.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer,
                File = file.DangerousGetHandle(),
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var nativeState = new NativeState(
                pathPointer,
                fileInfoPointer,
                new WinTrustData
                {
                    StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                    UiChoice = UiNone,
                    RevocationChecks = RevokeWholeChain,
                    UnionChoice = ChoiceFile,
                    FileInfo = fileInfoPointer,
                    StateAction = StateVerify,
                    ProviderFlags = CacheOnlyUrlRetrieval | RevocationCheckChainExcludeRoot,
                });
            var action = VerifyV2;
            var status = WinVerifyTrust(nint.Zero, ref action, ref nativeState.Data);
            return new(status, nativeState);
        }
        catch
        {
            if (fileInfoPointer != nint.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
            throw;
        }
    }

    public X509Certificate2 LoadSignerCertificate(WindowsTrustProviderState state)
    {
        var nativeState = RequireNativeState(state);
        var providerData = WTHelperProvDataFromStateData(nativeState.Data.StateData);
        if (providerData == nint.Zero)
            throw new CryptographicException();
        var signerPointer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signerPointer == nint.Zero)
            throw new CryptographicException();
        var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
        if (signer.CertChainCount == 0 || signer.CertChain == nint.Zero)
            throw new CryptographicException();
        var providerCertificate = Marshal.PtrToStructure<CryptProviderCertificate>(signer.CertChain);
        if (providerCertificate.Certificate == nint.Zero)
            throw new CryptographicException();
        var context = Marshal.PtrToStructure<CertificateContext>(providerCertificate.Certificate);
        if (context.EncodedCertificate == nint.Zero || context.EncodedCertificateBytes == 0 ||
            context.EncodedCertificateBytes > 1_048_576)
        {
            throw new CryptographicException();
        }
        var encoded = new byte[checked((int)context.EncodedCertificateBytes)];
        Marshal.Copy(context.EncodedCertificate, encoded, 0, encoded.Length);
        return X509CertificateLoader.LoadCertificate(encoded);
    }

    public bool Close(WindowsTrustProviderState state)
    {
        var nativeState = RequireNativeState(state);
        if (nativeState.Closed)
            return false;
        try
        {
            if (nativeState.Data.StateData == nint.Zero)
                return state.Status != 0;
            nativeState.Data.StateAction = StateClose;
            var action = VerifyV2;
            return WinVerifyTrust(nint.Zero, ref action, ref nativeState.Data) == 0;
        }
        finally
        {
            nativeState.Closed = true;
            Marshal.FreeHGlobal(nativeState.FileInfoPointer);
            Marshal.FreeCoTaskMem(nativeState.PathPointer);
        }
    }

    private static NativeState RequireNativeState(WindowsTrustProviderState state) =>
        state.NativeState as NativeState ?? throw new InvalidOperationException();

    [LibraryImport("wintrust.dll", SetLastError = true)]
    private static partial int WinVerifyTrust(nint window, ref Guid actionId, ref WinTrustData trustData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal nint FilePath;
        internal nint File;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructSize;
        internal FileTime VerifyAsOf;
        internal uint CertChainCount;
        internal nint CertChain;
        internal uint SignerType;
        internal nint Signer;
        internal uint Error;
        internal uint CounterSignerCount;
        internal nint CounterSigners;
        internal nint ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        internal uint StructSize;
        internal nint Certificate;
        internal int Commercial;
        internal int TrustedRoot;
        internal int SelfSigned;
        internal int TestCertificate;
        internal uint RevokedReason;
        internal uint Confidence;
        internal uint Error;
        internal nint TrustListContext;
        internal int TrustListSignerCertificate;
        internal nint CtlContext;
        internal uint CtlError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal nint EncodedCertificate;
        internal uint EncodedCertificateBytes;
        internal nint CertificateInfo;
        internal nint CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    private sealed class NativeState(
        nint pathPointer,
        nint fileInfoPointer,
        WinTrustData data)
    {
        internal nint PathPointer { get; } = pathPointer;
        internal nint FileInfoPointer { get; } = fileInfoPointer;
        internal WinTrustData Data = data;
        internal bool Closed { get; set; }
    }
}
