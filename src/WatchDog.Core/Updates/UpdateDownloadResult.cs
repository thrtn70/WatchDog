using System.Security.Cryptography;

namespace WatchDog.Core.Updates;

public enum UpdateVerificationFailure
{
    None,
    DownloadFailed,
    MissingManifest,
    SignatureInvalid,
    ManifestInvalid,
    ReleaseMismatch,
    Downgrade,
    InstallerMismatch,

    /// <summary>The user cancelled the download; not an error worth warning about.</summary>
    Cancelled,
}

/// <summary>
/// Outcome of downloading an update. <see cref="InstallerPath"/> is set only when the installer
/// bytes matched a manifest signed by the pinned release key; every other outcome leaves nothing
/// on disk and must not launch anything.
/// </summary>
public sealed record UpdateDownloadResult
{
    private readonly UpdateManifest? _manifest;

    private UpdateDownloadResult(string? installerPath, UpdateVerificationFailure failure, UpdateManifest? manifest)
    {
        InstallerPath = installerPath;
        Failure = failure;
        _manifest = manifest;
    }

    public string? InstallerPath { get; }

    public UpdateVerificationFailure Failure { get; }

    public bool IsVerified => Failure == UpdateVerificationFailure.None && InstallerPath is not null;

    public string RefusalMessage => Failure switch
    {
        UpdateVerificationFailure.None => string.Empty,
        UpdateVerificationFailure.DownloadFailed =>
            "The update couldn't be downloaded, so it wasn't installed. Your current version is unchanged.",
        UpdateVerificationFailure.MissingManifest =>
            "This release has no signed update manifest, so WatchDog can't verify it and it wasn't installed. " +
            "Your current version is unchanged.",
        UpdateVerificationFailure.Cancelled =>
            "The update download was cancelled, so it wasn't installed. Your current version is unchanged.",
        _ => "WatchDog couldn't verify that this update came from the WatchDog release, so it wasn't installed. " +
            "Your current version is unchanged.",
    };

    /// <summary>
    /// Opens the verified installer so it cannot be written or deleted, then re-checks its length and
    /// SHA-256 against the signed manifest. Keep the returned handle until the installer process has
    /// started, so the bytes that run are the bytes that were verified. Null if the file changed or is gone.
    /// </summary>
    public IDisposable? LockForLaunch()
    {
        if (!IsVerified || _manifest is null) return null;

        FileStream? stream = null;
        try
        {
            stream = new FileStream(InstallerPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == _manifest.Length &&
                string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), _manifest.Sha256, StringComparison.Ordinal))
            {
                return stream;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        stream?.Dispose();
        return null;
    }

    internal static UpdateDownloadResult Verified(string installerPath, UpdateManifest manifest) =>
        new(installerPath, UpdateVerificationFailure.None, manifest);

    public static UpdateDownloadResult Refused(UpdateVerificationFailure failure)
    {
        if (failure == UpdateVerificationFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure), "A refusal needs a failure reason.");
        return new(null, failure, null);
    }
}
