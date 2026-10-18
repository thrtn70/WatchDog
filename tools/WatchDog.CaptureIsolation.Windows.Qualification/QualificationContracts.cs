using System.Text.Json.Serialization;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static class QualificationConstants
{
    internal const string OptInToken = "WATCHDOG-RUN-NATIVE-ISOLATION-QUALIFICATION";
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(40);
    internal static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan ChildEmergencyExpiry = TimeSpan.FromSeconds(120);
    internal const string Digest = "89abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567";
}

internal enum QualificationExitCode
{
    Pass = 0,
    UsageOrPrerequisite = 2,
    AssertionFailedCleanupConfirmed = 3,
    TimeoutCleanupConfirmed = 4,
    CleanupIncomplete = 5,
    InfrastructureExceptionCleanupConfirmed = 6,
}

internal enum QualificationRole
{
    Runner,
    Owner,
    Helper,
    Descendant,
}

internal enum QualificationScenario
{
    ExplicitDescendantTermination,
    OwnerDeath,
    PostCreateCancellation,
    AuthenticatedBootstrap,
    RejectedBootstrapMaterial,
    RejectedUnintendedPeer,
    RejectedPartialHandshake,
    HandleRelativeMediaRoot,
    ConfinedMediaRootMutation,
    AtomicPublicationProbe,
    DistinctRootRenameProbe,
}

internal sealed record QualificationOptions(
    QualificationRole Role,
    QualificationScenario? Scenario,
    Guid Nonce,
    string ArtifactRoot,
    string ExecutablePath,
    string RepoHead,
    string DirtySummaryHash,
    string SourceHash,
    string BinaryHash,
    string QualificationAssemblyHash,
    string CaptureIsolationWindowsHash,
    string CaptureIsolationHash,
    string CaptureProtocolHash,
    long BootstrapSectionHandle,
    long BootstrapParentHandle,
    int BootstrapSectionBytes,
    long SentinelHandle,
    bool PublicationOnly,
    bool DistinctRootOnly)
{
    internal static bool TryParse(string[] args, out QualificationOptions? options)
    {
        options = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                return false;
            if (!values.TryAdd(args[index][2..], args[index + 1]))
                return false;
        }

        if (!values.TryGetValue("opt-in", out var optIn) ||
            optIn != QualificationConstants.OptInToken ||
            !values.TryGetValue("mode", out var roleValue) ||
            !values.TryGetValue("nonce", out var nonceValue) ||
            !Guid.TryParseExact(nonceValue, "D", out var nonce) || nonce == Guid.Empty ||
            !values.TryGetValue("artifact-root", out var artifactRoot) ||
            string.IsNullOrWhiteSpace(artifactRoot) || !Path.IsPathFullyQualified(artifactRoot) ||
            !values.TryGetValue("executable", out var executablePath) ||
            string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return false;
        }

        var role = roleValue.ToLowerInvariant() switch
        {
            "runner" => QualificationRole.Runner,
            "owner" => QualificationRole.Owner,
            "helper" => QualificationRole.Helper,
            "descendant" => QualificationRole.Descendant,
            _ => (QualificationRole)(-1),
        };
        if (!Enum.IsDefined(role))
            return false;

        QualificationScenario? scenario = null;
        if (role != QualificationRole.Runner)
        {
            if (!values.TryGetValue("scenario", out var scenarioValue))
            {
                return false;
            }
            var parsedScenario = scenarioValue.ToLowerInvariant() switch
            {
                "explicitdescendanttermination" =>
                    QualificationScenario.ExplicitDescendantTermination,
                "ownerdeath" => QualificationScenario.OwnerDeath,
                "postcreatecancellation" => QualificationScenario.PostCreateCancellation,
                "authenticatedbootstrap" => QualificationScenario.AuthenticatedBootstrap,
                "rejectedbootstrapmaterial" => QualificationScenario.RejectedBootstrapMaterial,
                "rejectedunintendedpeer" => QualificationScenario.RejectedUnintendedPeer,
                "rejectedpartialhandshake" => QualificationScenario.RejectedPartialHandshake,
                "handlerelativemediaroot" => QualificationScenario.HandleRelativeMediaRoot,
                "confinedmediarootmutation" => QualificationScenario.ConfinedMediaRootMutation,
                "atomicpublicationprobe" => QualificationScenario.AtomicPublicationProbe,
                "distinctrootrenameprobe" => QualificationScenario.DistinctRootRenameProbe,
                _ => (QualificationScenario)(-1),
            };
            if (!Enum.IsDefined(parsedScenario))
                return false;
            scenario = parsedScenario;
        }

        try
        {
            var sectionHandle = ParseOptionalInt64(values, "bootstrap-section");
            var parentHandle = ParseOptionalInt64(values, "bootstrap-parent");
            var sectionBytes = ParseOptionalInt32(values, "bootstrap-bytes");
            var sentinelHandle = ParseOptionalInt64(values, "sentinel-handle");
            if (sectionHandle < 0 || parentHandle < 0 || sectionBytes < 0 || sentinelHandle < 0)
                return false;
            var publicationOnly = values.GetValueOrDefault("publication-only") switch
            {
                null => false,
                "true" when role == QualificationRole.Runner => true,
                _ => throw new ArgumentException("Invalid publication-only option."),
            };
            var distinctRootOnly = values.GetValueOrDefault("distinct-root-only") switch
            {
                null => false,
                "true" when role == QualificationRole.Runner => true,
                _ => throw new ArgumentException("Invalid distinct-root-only option."),
            };
            if (publicationOnly && distinctRootOnly)
                return false;
            var qualificationHash = values.GetValueOrDefault("qualification-assembly-hash", string.Empty);
            var windowsHash = values.GetValueOrDefault("capture-isolation-windows-hash", string.Empty);
            var isolationHash = values.GetValueOrDefault("capture-isolation-hash", string.Empty);
            var protocolHash = values.GetValueOrDefault("capture-protocol-hash", string.Empty);
            if (role == QualificationRole.Runner)
            {
                if (!FixtureBinaryAttribution.IsSha256(qualificationHash) ||
                    !FixtureBinaryAttribution.IsSha256(windowsHash) ||
                    !FixtureBinaryAttribution.IsSha256(isolationHash) ||
                    !FixtureBinaryAttribution.IsSha256(protocolHash))
                    return false;
            }
            else if (values.ContainsKey("qualification-assembly-hash") ||
                values.ContainsKey("capture-isolation-windows-hash") ||
                values.ContainsKey("capture-isolation-hash") ||
                values.ContainsKey("capture-protocol-hash"))
            {
                return false;
            }
            options = new(
                role,
                scenario,
                nonce,
                Path.GetFullPath(artifactRoot),
                Path.GetFullPath(executablePath),
                values.GetValueOrDefault("repo-head", string.Empty),
                values.GetValueOrDefault("dirty-summary-hash", string.Empty),
                values.GetValueOrDefault("source-hash", string.Empty),
                values.GetValueOrDefault("binary-hash", string.Empty),
                qualificationHash.ToLowerInvariant(),
                windowsHash.ToLowerInvariant(),
                isolationHash.ToLowerInvariant(),
                protocolHash.ToLowerInvariant(),
                sectionHandle,
                parentHandle,
                sectionBytes,
                sentinelHandle,
                publicationOnly,
                distinctRootOnly);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static long ParseOptionalInt64(IReadOnlyDictionary<string, string> values, string key) =>
        !values.TryGetValue(key, out var value) ? 0 :
        long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;

    private static int ParseOptionalInt32(IReadOnlyDictionary<string, string> values, string key) =>
        !values.TryGetValue(key, out var value) ? 0 :
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
}

internal sealed record RoleRecord(
    DateTimeOffset TimestampUtc,
    long ElapsedMilliseconds,
    Guid Nonce,
    string Role,
    string Scenario,
    string Event,
    string? Code = null,
    uint? ProcessId = null);

internal sealed record ProcessCoordinate(uint ProcessId, long CreationFileTime);

internal sealed record HelperReadyRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    ProcessCoordinate Descendant,
    bool NoMoreSpawn);

internal sealed record OwnerArmedRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    ProcessCoordinate Helper,
    ProcessCoordinate Descendant,
    bool InnerMembershipExact,
    bool NoMoreSpawn);

internal sealed record OwnerResultRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    bool Passed,
    string Code);

internal sealed record BootstrapReadyRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    bool Authenticated,
    bool AuthenticatedHelloSent,
    bool SentinelUnavailable);

internal sealed record MembershipSnapshotRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    IReadOnlyList<uint> ExpectedProcessIds,
    uint AssignedProcessCount,
    uint MemberProcessCount,
    bool Truncated,
    IReadOnlyList<uint> ActualProcessIds,
    uint ActiveProcessCount,
    bool HelperIsMember,
    bool DescendantIsMember,
    string ObservedPolicy);

internal sealed record DiagnosticJobCensusRecord(
    uint AssignedProcessCount,
    uint ListedProcessCount,
    uint ActiveProcessCount,
    bool Complete,
    IReadOnlyList<uint> ProcessIds,
    string? FailureCode);

internal sealed record DiagnosticProcessIdentityRecord(
    uint ProcessId,
    long? CreationFileTime,
    string? ImagePath,
    string? NormalizedImagePathSha256,
    bool? MatchesFixtureImage,
    bool? IsMemberOfExactJob,
    string Source,
    string Status);

internal sealed record MembershipEnumerationRecord(
    Guid Nonce,
    string Role,
    string Scenario,
    DiagnosticJobCensusRecord FirstCensus,
    DiagnosticJobCensusRecord? SecondCensus,
    bool Stable,
    IReadOnlyList<DiagnosticProcessIdentityRecord> Processes,
    string? FailureCode);

internal sealed record ScenarioResultRecord(
    string Scenario,
    bool Passed,
    string Code,
    long ElapsedMilliseconds,
    bool CleanupConfirmed);

internal sealed record RunResultRecord(
    Guid Nonce,
    bool Passed,
    int ExitCode,
    long ElapsedMilliseconds,
    IReadOnlyList<ScenarioResultRecord> Scenarios);

internal sealed record ManifestRecord(
    Guid Nonce,
    DateTimeOffset StartedUtc,
    string RepoHead,
    string DirtySummaryHash,
    string SourceHash,
    string BinaryHash,
    string ObservedSubsystem,
    string AppHostSha256,
    string QualificationAssemblySha256,
    string CaptureIsolationWindowsSha256,
    string CaptureIsolationSha256,
    string CaptureProtocolSha256,
    string ExpectedQualificationAssemblySha256,
    string ExpectedCaptureIsolationWindowsSha256,
    string ExpectedCaptureIsolationSha256,
    string ExpectedCaptureProtocolSha256,
    Guid QualificationAssemblyMvid,
    Guid CaptureIsolationWindowsMvid,
    Guid CaptureIsolationMvid,
    Guid CaptureProtocolMvid,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    bool? RunnerWasAlreadyInJob,
    IReadOnlyDictionary<string, int> FixedTimeoutSeconds,
    IReadOnlyDictionary<string, int> FixedBootstrapLimits);

[JsonSerializable(typeof(RoleRecord))]
[JsonSerializable(typeof(HelperReadyRecord))]
[JsonSerializable(typeof(OwnerArmedRecord))]
[JsonSerializable(typeof(OwnerResultRecord))]
[JsonSerializable(typeof(BootstrapReadyRecord))]
[JsonSerializable(typeof(MembershipSnapshotRecord))]
[JsonSerializable(typeof(DiagnosticJobCensusRecord))]
[JsonSerializable(typeof(DiagnosticProcessIdentityRecord))]
[JsonSerializable(typeof(MembershipEnumerationRecord))]
[JsonSerializable(typeof(ScenarioResultRecord))]
[JsonSerializable(typeof(RunResultRecord))]
[JsonSerializable(typeof(ManifestRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class QualificationJsonContext : JsonSerializerContext;
