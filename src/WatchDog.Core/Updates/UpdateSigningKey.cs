namespace WatchDog.Core.Updates;

/// <summary>
/// Public half of the release update-signing key (ECDSA P-256, SubjectPublicKeyInfo, base64).
/// The private half exists only outside the repository and in the WATCHDOG_UPDATE_SIGNING_KEY
/// release secret (see tools/sign-update-manifest.ps1). Replacing this key makes every install
/// that pins the old one refuse newer releases, so rotate only with a manual-reinstall plan.
/// </summary>
internal static class UpdateSigningKey
{
    internal const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELtv5AseGOGJhn7AdOagxHYEf6l60AzIG40z5zPxUbPnpq08O9W27xEzXjRxxasHSbGRk3IJ5RPdluiNBoO7c7A==";
}
