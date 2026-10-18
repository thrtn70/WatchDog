using System.Diagnostics;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static class QualificationRoles
{
    internal static async Task<QualificationExitCode> RunOwnerAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        if (options.Scenario is not { } scenario)
            return QualificationExitCode.UsageOrPrerequisite;

        FixtureOwnedLaunch? inner = null;
        RetainedProcessProof? descendant = null;
        var cleanupConfirmed = false;
        try
        {
            inner = await FixtureOwnedLaunch.CreateAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper, scenario),
                cancellationToken).ConfigureAwait(false);
            artifacts.Log("inner-created", processId: inner.Coordinate.ProcessId);
            await inner.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            artifacts.Log("inner-helper-resumed");

            var ready = await artifacts.WaitForJsonAsync(
                "helper-ready.json",
                QualificationJsonContext.Default.HelperReadyRecord,
                QualificationConstants.ReadinessTimeout,
                cancellationToken).ConfigureAwait(false);
            RequireHelperReady(options, scenario, ready);
            descendant = RetainedProcessProof.OpenOnceAndVerify(
                ready.Descendant,
                options.ExecutablePath);
            var enumeration = inner.CaptureDiagnosticMembership(
                options.Nonce,
                scenario.ToString(),
                descendant,
                options.ExecutablePath);
            artifacts.WriteJson(
                "membership-enumeration.json",
                enumeration,
                QualificationJsonContext.Default.MembershipEnumerationRecord);
            var membership = inner.CaptureMembershipSnapshot(
                options.Nonce,
                scenario.ToString(),
                descendant);
            artifacts.WriteJson(
                "membership-snapshot.json",
                membership,
                QualificationJsonContext.Default.MembershipSnapshotRecord);
            if (!enumeration.Stable || enumeration.FailureCode is not null)
            {
                throw new QualificationAssertionException(
                    enumeration.FailureCode ?? "fixture.job.diagnostic-census-unstable");
            }
            FixtureOwnedLaunch.AssertExactMembership(membership);
            artifacts.Log("inner-membership-exact");

            if (scenario == QualificationScenario.OwnerDeath)
            {
                artifacts.WriteJson(
                    "owner-armed.json",
                    new OwnerArmedRecord(
                        options.Nonce,
                        "owner",
                        scenario.ToString(),
                        inner.Coordinate,
                        ready.Descendant,
                        true,
                        ready.NoMoreSpawn),
                    QualificationJsonContext.Default.OwnerArmedRecord);
                artifacts.Log("owner-armed");

                // Expected termination is by the runner's exact retained owner handle. If that
                // does not happen, expire safely after the fixed emergency interval.
                await Task.Delay(QualificationConstants.ChildEmergencyExpiry, cancellationToken)
                    .ConfigureAwait(false);
                throw new QualificationTimeoutException("fixture.owner.emergency-expired");
            }

            if (scenario != QualificationScenario.ExplicitDescendantTermination)
                throw new QualificationAssertionException("fixture.owner.scenario-invalid");

            await inner.TerminateJobExactAsync().ConfigureAwait(false);
            await inner.ReapAsync(
                QualificationConstants.TerminalTimeout,
                cancellationToken).ConfigureAwait(false);
            await descendant.WaitForExitAsync(
                QualificationConstants.TerminalTimeout,
                cancellationToken).ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("inner-explicit-termination-terminal");
            await inner.DisposeAsync().ConfigureAwait(false);
            inner = null;
            descendant.Dispose();
            descendant = null;

            artifacts.WriteJson(
                "owner-result.json",
                new OwnerResultRecord(
                    options.Nonce,
                    "owner",
                    scenario.ToString(),
                    true,
                    "passed"),
                QualificationJsonContext.Default.OwnerResultRecord);
            return QualificationExitCode.Pass;
        }
        catch (Exception exception)
        {
            var code = ExceptionCode(exception);
            artifacts.WriteJson(
                "owner-result.json",
                new OwnerResultRecord(
                    options.Nonce,
                    "owner",
                    scenario.ToString(),
                    false,
                    code),
                QualificationJsonContext.Default.OwnerResultRecord);
            throw;
        }
        finally
        {
            descendant?.Dispose();
            if (inner is not null)
            {
                if (!inner.IsReaped)
                {
                    try
                    {
                        await inner.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                        artifacts.Log("inner-emergency-cleanup-confirmed");
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.inner.cleanup-incomplete");
                    }
                }
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && inner is not null)
                throw new QualificationCleanupException("fixture.inner.cleanup-incomplete");
        }
    }

    internal static async Task<QualificationExitCode> RunHelperAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        if (options.Scenario is not { } scenario)
            return QualificationExitCode.UsageOrPrerequisite;

        if (scenario is QualificationScenario.AuthenticatedBootstrap or
            QualificationScenario.RejectedBootstrapMaterial or
            QualificationScenario.RejectedUnintendedPeer or
            QualificationScenario.RejectedPartialHandshake)
        {
            if (options.BootstrapSectionHandle <= 0 ||
                options.BootstrapParentHandle <= 0 ||
                options.BootstrapSectionBytes <= 0 ||
                options.SentinelHandle <= 0)
            {
                return QualificationExitCode.UsageOrPrerequisite;
            }
            var sentinelUnavailable = WindowsBootstrapHelperSession.IsHandleUnavailable(
                checked((nint)options.SentinelHandle));
            WindowsBootstrapHelperSession? bootstrap = null;
            try
            {
                bootstrap = await WindowsBootstrapHelperSession.ConnectAsync(
                    checked((nint)options.BootstrapSectionHandle),
                    checked((nint)options.BootstrapParentHandle),
                    options.BootstrapSectionBytes,
                    BootstrapProfile(),
                    cancellationToken,
                    abortAfterHelloForFixture:
                        scenario == QualificationScenario.RejectedPartialHandshake)
                    .ConfigureAwait(false);
            }
            catch (HelperBoundaryException exception) when (
                FixtureBootstrapRejection.IsExpected(scenario, exception.FailureCode))
            {
                artifacts.WriteJson(
                    "bootstrap-ready.json",
                    new BootstrapReadyRecord(
                        options.Nonce,
                        "helper",
                        scenario.ToString(),
                        false,
                        scenario == QualificationScenario.RejectedPartialHandshake,
                        sentinelUnavailable),
                    QualificationJsonContext.Default.BootstrapReadyRecord);
                artifacts.Log("bootstrap-handshake-rejected");
                return sentinelUnavailable
                    ? QualificationExitCode.Pass
                    : QualificationExitCode.AssertionFailedCleanupConfirmed;
            }
            await using var ownedBootstrap = bootstrap;
            artifacts.WriteJson(
                "bootstrap-ready.json",
                new BootstrapReadyRecord(
                    options.Nonce,
                    "helper",
                    scenario.ToString(),
                    true,
                    true,
                    sentinelUnavailable),
                QualificationJsonContext.Default.BootstrapReadyRecord);
            artifacts.Log("bootstrap-authenticated");
            if (scenario == QualificationScenario.RejectedBootstrapMaterial)
                return QualificationExitCode.AssertionFailedCleanupConfirmed;
            await ownedBootstrap.WaitForFenceAsync(cancellationToken).ConfigureAwait(false);
            return sentinelUnavailable
                ? QualificationExitCode.Pass
                : QualificationExitCode.AssertionFailedCleanupConfirmed;
        }

        artifacts.WriteJson(
            "helper-startup.json",
            new RoleRecord(
                DateTimeOffset.UtcNow,
                0,
                options.Nonce,
                "helper",
                scenario.ToString(),
                "startup-after-resume"),
            QualificationJsonContext.Default.RoleRecord);
        artifacts.Log("helper-startup-after-resume");

        using var descendant = FixtureDescendantLaunch.Create(
            options.ExecutablePath,
            ChildArguments(options, QualificationRole.Descendant, scenario));
        var coordinate = descendant.Coordinate;
        var descendantStartup = await artifacts.WaitForJsonAsync(
            "descendant-startup.json",
            QualificationJsonContext.Default.RoleRecord,
            QualificationConstants.ReadinessTimeout,
            cancellationToken).ConfigureAwait(false);
        RequireDescendantStartup(options, scenario, descendantStartup);
        artifacts.WriteJson(
            "helper-ready.json",
            new HelperReadyRecord(
                options.Nonce,
                "helper",
                scenario.ToString(),
                coordinate,
                true),
            QualificationJsonContext.Default.HelperReadyRecord);
        artifacts.Log("descendant-spawned-and-sealed", processId: coordinate.ProcessId);

        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.ChildEmergencyExpiry.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descendant.IsExited)
                throw new QualificationAssertionException("fixture.descendant.exited-early");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        if (!descendant.IsExited)
        {
            descendant.TerminateEmergencyExact();
            await descendant.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        return QualificationExitCode.TimeoutCleanupConfirmed;
    }

    internal static async Task<QualificationExitCode> RunDescendantAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        if (options.Scenario is not { } scenario)
            return QualificationExitCode.UsageOrPrerequisite;
        artifacts.WriteJson(
            "descendant-startup.json",
            new RoleRecord(
                DateTimeOffset.UtcNow,
                0,
                options.Nonce,
                "descendant",
                scenario.ToString(),
                "startup"),
            QualificationJsonContext.Default.RoleRecord);
        artifacts.Log("descendant-startup");
        await Task.Delay(QualificationConstants.ChildEmergencyExpiry, cancellationToken)
            .ConfigureAwait(false);
        return QualificationExitCode.TimeoutCleanupConfirmed;
    }

    private static IReadOnlyList<string> ChildArguments(
        QualificationOptions options,
        QualificationRole role,
        QualificationScenario scenario) =>
        [
            "--opt-in", QualificationConstants.OptInToken,
            "--mode", role.ToString().ToLowerInvariant(),
            "--scenario", scenario.ToString(),
            "--nonce", options.Nonce.ToString("D"),
            "--artifact-root", options.ArtifactRoot,
            "--executable", options.ExecutablePath,
        ];

    private static void RequireHelperReady(
        QualificationOptions options,
        QualificationScenario scenario,
        HelperReadyRecord ready)
    {
        if (ready.Nonce != options.Nonce ||
            !ready.Role.Equals("helper", StringComparison.Ordinal) ||
            !ready.Scenario.Equals(scenario.ToString(), StringComparison.Ordinal) ||
            !ready.NoMoreSpawn)
        {
            throw new QualificationAssertionException("fixture.coordination.foreign");
        }
    }

    private static void RequireDescendantStartup(
        QualificationOptions options,
        QualificationScenario scenario,
        RoleRecord startup)
    {
        if (startup.Nonce != options.Nonce ||
            !startup.Role.Equals("descendant", StringComparison.Ordinal) ||
            !startup.Scenario.Equals(scenario.ToString(), StringComparison.Ordinal) ||
            !startup.Event.Equals("startup", StringComparison.Ordinal))
        {
            throw new QualificationAssertionException("fixture.coordination.foreign");
        }
    }

    private static string ExceptionCode(Exception exception) => exception switch
    {
        QualificationAssertionException assertion => assertion.Code,
        QualificationTimeoutException timeout => timeout.Code,
        QualificationCleanupException cleanup => cleanup.Code,
        _ => "fixture.infrastructure.failed",
    };

    internal static WindowsBootstrapFixtureProfile BootstrapProfile() => new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5),
        1,
        new CaptureProtocolLimits(4096, 2048, 512, 16, 32, 512, 12));
}
