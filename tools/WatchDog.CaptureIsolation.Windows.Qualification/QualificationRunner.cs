using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static class QualificationRunner
{
    internal static async Task<QualificationExitCode> RunAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        Stopwatch clock)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !File.Exists(options.ExecutablePath) ||
            !Path.GetFullPath(options.ExecutablePath).Equals(
                Path.GetFullPath(Environment.ProcessPath ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            return QualificationExitCode.UsageOrPrerequisite;
        }

        FixtureBinaryEvidence binaryEvidence;
        try
        {
            binaryEvidence = CaptureBinaryEvidence(options);
        }
        catch (FixturePrerequisiteException exception)
        {
            artifacts.Log("binary-prerequisite-failed", exception.Code);
            return QualificationExitCode.UsageOrPrerequisite;
        }
        catch
        {
            artifacts.Log("binary-evidence-failed", "fixture.binary.infrastructure-failed");
            return QualificationExitCode.InfrastructureExceptionCleanupConfirmed;
        }

        var passedBinaryHash = ManifestHash(options.BinaryHash, 64);

        artifacts.WriteJson(
            "manifest.json",
            new ManifestRecord(
                options.Nonce,
                DateTimeOffset.UtcNow,
                ManifestHash(options.RepoHead, 40),
                ManifestHash(options.DirtySummaryHash, 64),
                ManifestHash(options.SourceHash, 64),
                passedBinaryHash,
                binaryEvidence.Subsystem.ToString(),
                binaryEvidence.AppHostSha256,
                binaryEvidence.QualificationAssemblySha256,
                binaryEvidence.CaptureIsolationWindowsSha256,
                binaryEvidence.CaptureIsolationSha256,
                binaryEvidence.CaptureProtocolSha256,
                options.QualificationAssemblyHash,
                options.CaptureIsolationWindowsHash,
                options.CaptureIsolationHash,
                options.CaptureProtocolHash,
                binaryEvidence.QualificationAssemblyMvid,
                binaryEvidence.CaptureIsolationWindowsMvid,
                binaryEvidence.CaptureIsolationMvid,
                binaryEvidence.CaptureProtocolMvid,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RetainedProcessProof.CurrentProcessWasAlreadyInJob(),
                new Dictionary<string, int>
                {
                    ["readiness"] = 10,
                    ["terminal"] = 10,
                    ["cleanup"] = 15,
                    ["scenario"] = 40,
                    ["run"] = 180,
                    ["childEmergency"] = 120,
                },
                new Dictionary<string, int>
                {
                    ["handshakeSeconds"] = 10,
                    ["drainSeconds"] = 5,
                    ["queueDepth"] = 1,
                    ["frameBytes"] = 4096,
                    ["payloadBytes"] = 2048,
                    ["pendingCommands"] = 8,
                    ["telemetrySamples"] = 32,
                }),
            QualificationJsonContext.Default.ManifestRecord);

        if (binaryEvidence.Subsystem != Subsystem.WindowsGui ||
            passedBinaryHash == "unavailable" ||
            !passedBinaryHash.Equals(binaryEvidence.AppHostSha256, StringComparison.Ordinal))
        {
            artifacts.Log("binary-prerequisite-failed", "fixture.binary.identity-mismatch");
            return QualificationExitCode.UsageOrPrerequisite;
        }

        using var runTimeout = new CancellationTokenSource(QualificationConstants.RunTimeout);
        var results = new List<ScenarioResultRecord>();
        QualificationScenario[] scenarios = options.PublicationOnly
            ? [QualificationScenario.AtomicPublicationProbe]
            : options.DistinctRootOnly
                ? [QualificationScenario.DistinctRootRenameProbe]
                : Enum.GetValues<QualificationScenario>()
                    .Where(static scenario => scenario is not
                        (QualificationScenario.AtomicPublicationProbe or
                            QualificationScenario.DistinctRootRenameProbe))
                    .ToArray();
        foreach (var scenario in scenarios)
        {
            if (runTimeout.IsCancellationRequested)
            {
                results.Add(new(
                    scenario.ToString(),
                    false,
                    "fixture.run.timeout",
                    clock.ElapsedMilliseconds,
                    true));
                break;
            }
            results.Add(await RunScenarioAsync(options, scenario, runTimeout.Token)
                .ConfigureAwait(false));
        }

        var exitCode = ExitCodeFor(results, scenarios.Length);
        var runResult = new RunResultRecord(
            options.Nonce,
            exitCode == QualificationExitCode.Pass,
            (int)exitCode,
            clock.ElapsedMilliseconds,
            results.AsReadOnly());
        artifacts.WriteJson("result.json", runResult, QualificationJsonContext.Default.RunResultRecord);
        QualificationArtifacts.WriteTextAtomic(
            artifacts.PathFor("report.md"),
            BuildReport(runResult));
        artifacts.Log("run-complete", $"exit-{(int)exitCode}");
        return exitCode;
    }

    private static async Task<ScenarioResultRecord> RunScenarioAsync(
        QualificationOptions options,
        QualificationScenario scenario,
        CancellationToken runCancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var scenarioRoot = Path.Combine(
            options.ArtifactRoot,
            scenario.ToString().ToLowerInvariant());
        Directory.CreateDirectory(scenarioRoot);
        using var scenarioTimeout = CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);
        scenarioTimeout.CancelAfter(QualificationConstants.ScenarioTimeout);
        var scenarioOptions = options with
        {
            Scenario = scenario,
            ArtifactRoot = scenarioRoot,
        };
        var artifacts = new QualificationArtifacts(scenarioOptions, stopwatch);
        artifacts.Log("scenario-start");

        try
        {
            var result = scenario switch
            {
                QualificationScenario.ExplicitDescendantTermination =>
                    await RunOwnerScenarioAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.OwnerDeath =>
                    await RunOwnerDeathAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.PostCreateCancellation =>
                    await RunPostCreateCancellationAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.AuthenticatedBootstrap =>
                    await RunAuthenticatedBootstrapAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.RejectedBootstrapMaterial =>
                    await RunRejectedBootstrapMaterialAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.RejectedUnintendedPeer =>
                    await RunRejectedUnintendedPeerAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.RejectedPartialHandshake =>
                    await RunRejectedPartialHandshakeAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.HandleRelativeMediaRoot =>
                    await RunHandleRelativeMediaRootAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.ConfinedMediaRootMutation =>
                    await RunConfinedMediaRootMutationAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.AtomicPublicationProbe =>
                    await PublicationRenameProbe.RunAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                QualificationScenario.DistinctRootRenameProbe =>
                    await DistinctRootRenameProbe.RunAsync(
                        scenarioOptions,
                        artifacts,
                        scenarioTimeout.Token).ConfigureAwait(false),
                _ => throw new QualificationAssertionException("fixture.scenario.unknown"),
            };
            return result with { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }
        catch (QualificationCleanupException exception)
        {
            artifacts.Log("scenario-cleanup-uncertain", exception.Code);
            return new(scenario.ToString(), false, exception.Code, stopwatch.ElapsedMilliseconds, false);
        }
        catch (QualificationTimeoutException exception)
        {
            artifacts.Log("scenario-timeout", exception.Code);
            return new(scenario.ToString(), false, exception.Code, stopwatch.ElapsedMilliseconds, true);
        }
        catch (OperationCanceledException)
        {
            artifacts.Log("scenario-timeout", "fixture.scenario.timeout");
            return new(
                scenario.ToString(),
                false,
                "fixture.scenario.timeout",
                stopwatch.ElapsedMilliseconds,
                true);
        }
        catch (QualificationAssertionException exception)
        {
            artifacts.Log("scenario-assertion-failed", exception.Code);
            return new(scenario.ToString(), false, exception.Code, stopwatch.ElapsedMilliseconds, true);
        }
        catch (HelperBoundaryException exception)
        {
            artifacts.Log("scenario-boundary-failed", exception.FailureCode);
            return new(
                scenario.ToString(),
                false,
                exception.FailureCode,
                stopwatch.ElapsedMilliseconds,
                true);
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                PlatformNotSupportedException => "fixture.infrastructure.platform-not-supported",
                UnauthorizedAccessException => "fixture.infrastructure.access-denied",
                ArgumentException => "fixture.infrastructure.argument-invalid",
                IOException => "fixture.infrastructure.io-failed",
                _ => "fixture.infrastructure.failed",
            };
            artifacts.Log("scenario-infrastructure-failed", code);
            return new(
                scenario.ToString(),
                false,
                code,
                stopwatch.ElapsedMilliseconds,
                true);
        }
    }

    private static async Task<ScenarioResultRecord> RunHandleRelativeMediaRootAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(options.ArtifactRoot, "media-scratch");
        var rootPath = Path.Combine(scratch, "retained-root");
        Directory.CreateDirectory(rootPath);
        var cleanupConfirmed = false;
        var boundary = new WindowsMediaRootBoundary();
        try
        {
            var root = await boundary.AdmitAsync(rootPath, cancellationToken).ConfigureAwait(false);
            if (root.Status != LocalMediaRootAdmissionStatus.Confirmed || root.Identity is null)
                throw new QualificationAssertionException("fixture.media-root.admission-failed");

            var created = await boundary.CreateNewFileAsync(
                root.Identity,
                "fixture-probe.bin",
                cancellationToken).ConfigureAwait(false);
            if (created.Status != HandleRelativeMediaFileStatus.Created || created.Identity is null)
                throw new QualificationAssertionException("fixture.media-root.relative-create-failed");

            var expected = Encoding.UTF8.GetBytes("watchdog-handle-relative-native-proof");
            await boundary.WriteFileAsync(created.Identity, expected, cancellationToken)
                .ConfigureAwait(false);
            await boundary.FlushFileAsync(created.Identity, cancellationToken).ConfigureAwait(false);
            var observed = await boundary.ReadFileAsync(created.Identity, 4096, cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, observed))
                throw new QualificationAssertionException("fixture.media-root.readback-mismatch");

            await boundary.ReleaseFileAsync(created.Identity, CancellationToken.None)
                .ConfigureAwait(false);
            var collision = await boundary.CreateNewFileAsync(
                root.Identity,
                "fixture-probe.bin",
                cancellationToken).ConfigureAwait(false);
            if (collision.Status != HandleRelativeMediaFileStatus.AlreadyExists)
                throw new QualificationAssertionException("fixture.media-root.overwrite-not-rejected");

            await boundary.ReleaseAsync(root.Identity, CancellationToken.None).ConfigureAwait(false);
            await boundary.DisposeAsync().ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    expected,
                    File.ReadAllBytes(Path.Combine(rootPath, "fixture-probe.bin"))))
            {
                throw new QualificationAssertionException("fixture.media-root.final-bytes-mismatch");
            }
            artifacts.Log("media-root-handle-relative-confirmed");
            cleanupConfirmed = DeleteMediaScratch(scratch, reparsePath: null);
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, cleanupConfirmed);
        }
        finally
        {
            try
            {
                await boundary.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupConfirmed = false;
            }
            if (!cleanupConfirmed)
            {
                if (!DeleteMediaScratch(scratch, reparsePath: null))
                    throw new QualificationCleanupException("fixture.media-root.cleanup-failed");
            }
        }
    }

    private static async Task<ScenarioResultRecord> RunConfinedMediaRootMutationAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(options.ArtifactRoot, "mutation-scratch");
        var rootPath = Path.Combine(scratch, "retained-root");
        var outsidePath = Path.Combine(scratch, "outside-canary");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outsidePath);
        var canaryPath = Path.Combine(outsidePath, "canary.bin");
        var canary = Encoding.UTF8.GetBytes("outside-must-remain-unchanged");
        File.WriteAllBytes(canaryPath, canary);
        var junctionSet = false;
        var junctionRedirectConfirmed = false;
        var cleanupConfirmed = false;
        var relativeApi = new MutatingRelativeFileApi(
            WindowsRelativeFileApi.Instance,
            () =>
            {
                FixtureJunction.Set(rootPath, outsidePath);
                junctionSet = true;
                var redirectedCanary = Path.Combine(rootPath, "canary.bin");
                junctionRedirectConfirmed = File.Exists(redirectedCanary) &&
                    CryptographicOperations.FixedTimeEquals(
                        canary,
                        File.ReadAllBytes(redirectedCanary));
            });
        var boundary = new WindowsMediaRootBoundary(WindowsMediaRootApi.Instance, relativeApi);
        try
        {
            var root = await boundary.AdmitAsync(rootPath, cancellationToken).ConfigureAwait(false);
            if (root.Status != LocalMediaRootAdmissionStatus.Confirmed || root.Identity is null)
                throw new QualificationAssertionException("fixture.media-root.admission-failed");

            var result = await boundary.CreateNewFileAsync(
                root.Identity,
                "mutation-probe.bin",
                cancellationToken).ConfigureAwait(false);
            var confined = result.Status == HandleRelativeMediaFileStatus.Created &&
                result.Identity is not null;
            var rejected = result.Status == HandleRelativeMediaFileStatus.ReparsePoint;
            artifacts.Log($"media-root-mutation-status-{result.Status.ToString().ToLowerInvariant()}");
            if (!confined && !rejected)
                throw new QualificationAssertionException("fixture.media-root.mutation-not-confined");
            if (!junctionSet || !junctionRedirectConfirmed ||
                File.Exists(Path.Combine(outsidePath, "mutation-probe.bin")) ||
                !CryptographicOperations.FixedTimeEquals(canary, File.ReadAllBytes(canaryPath)))
            {
                throw new QualificationAssertionException("fixture.media-root.outside-canary-changed");
            }

            var confinedBytes = Encoding.UTF8.GetBytes("retained-root-only");
            if (confined)
            {
                await boundary.WriteFileAsync(result.Identity!, confinedBytes, cancellationToken)
                    .ConfigureAwait(false);
                await boundary.FlushFileAsync(result.Identity!, cancellationToken).ConfigureAwait(false);
                var readback = await boundary.ReadFileAsync(result.Identity!, 4096, cancellationToken)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(confinedBytes, readback))
                    throw new QualificationAssertionException("fixture.media-root.confined-readback-mismatch");
                await boundary.ReleaseFileAsync(result.Identity!, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            FixtureJunction.Delete(rootPath);
            junctionSet = false;
            var retainedProbe = Path.Combine(rootPath, "mutation-probe.bin");
            if (File.Exists(Path.Combine(outsidePath, "mutation-probe.bin")) ||
                !CryptographicOperations.FixedTimeEquals(canary, File.ReadAllBytes(canaryPath)) ||
                (rejected && File.Exists(retainedProbe)) ||
                (confined && (!File.Exists(retainedProbe) ||
                    !CryptographicOperations.FixedTimeEquals(
                        confinedBytes,
                        File.ReadAllBytes(retainedProbe)))))
            {
                throw new QualificationAssertionException("fixture.media-root.mutation-confinement-invalid");
            }
            await boundary.ReleaseAsync(root.Identity, CancellationToken.None).ConfigureAwait(false);
            await boundary.DisposeAsync().ConfigureAwait(false);
            artifacts.Log(confined
                ? "media-root-mutation-confined"
                : "media-root-mutation-rejected");
            cleanupConfirmed = DeleteMediaScratch(scratch, reparsePath: null);
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, cleanupConfirmed);
        }
        finally
        {
            try
            {
                if (junctionSet)
                {
                    FixtureJunction.Delete(rootPath);
                    junctionSet = false;
                }
                await boundary.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupConfirmed = false;
            }
            if (!cleanupConfirmed)
            {
                if (!DeleteMediaScratch(scratch, junctionSet ? rootPath : null))
                    throw new QualificationCleanupException("fixture.media-root.cleanup-failed");
            }
        }
    }

    private static bool DeleteMediaScratch(string scratch, string? reparsePath)
    {
        try
        {
            if (reparsePath is not null && Directory.Exists(reparsePath))
                FixtureJunction.Delete(reparsePath);
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
            return !Directory.Exists(scratch);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ScenarioResultRecord> RunOwnerScenarioAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? outer = null;
        var cleanupConfirmed = false;
        try
        {
            outer = await FixtureOwnedLaunch.CreateAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Owner),
                cancellationToken).ConfigureAwait(false);
            artifacts.Log("outer-created", processId: outer.Coordinate.ProcessId);
            await outer.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            artifacts.Log("outer-resumed");

            var ownerResult = await artifacts.WaitForJsonAsync(
                "owner-result.json",
                QualificationJsonContext.Default.OwnerResultRecord,
                QualificationConstants.ReadinessTimeout,
                cancellationToken).ConfigureAwait(false);
            RequireCoordinate(options, ownerResult.Nonce, ownerResult.Role, ownerResult.Scenario);
            if (!ownerResult.Passed)
                throw new QualificationAssertionException(ownerResult.Code);

            await outer.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("outer-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            if (outer is not null)
            {
                if (!outer.IsReaped)
                {
                    try
                    {
                        await outer.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                        artifacts.Log("outer-emergency-cleanup-confirmed");
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.outer.cleanup-incomplete");
                    }
                }
                await outer.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && outer is not null)
                throw new QualificationCleanupException("fixture.outer.cleanup-incomplete");
        }
    }

    private static async Task<ScenarioResultRecord> RunOwnerDeathAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? outer = null;
        RetainedProcessProof? helper = null;
        RetainedProcessProof? descendant = null;
        var cleanupConfirmed = false;
        try
        {
            outer = await FixtureOwnedLaunch.CreateAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Owner),
                cancellationToken).ConfigureAwait(false);
            await outer.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            var armed = await WaitForOwnerArmedAsync(
                options,
                artifacts,
                outer,
                cancellationToken).ConfigureAwait(false);
            RequireCoordinate(options, armed.Nonce, armed.Role, armed.Scenario);
            if (!armed.InnerMembershipExact || !armed.NoMoreSpawn)
                throw new QualificationAssertionException("fixture.owner.not-armed");

            // The runner receives only process coordinates. It never receives, duplicates, or
            // opens the owner's inner job handle.
            helper = RetainedProcessProof.OpenOnceAndVerify(armed.Helper, options.ExecutablePath);
            descendant = RetainedProcessProof.OpenOnceAndVerify(
                armed.Descendant,
                options.ExecutablePath);
            artifacts.Log("child-proofs-retained");

            outer.TerminateProcessExact();
            artifacts.Log("owner-terminated-exact");
            await helper.WaitForExitAsync(
                QualificationConstants.TerminalTimeout,
                cancellationToken).ConfigureAwait(false);
            await descendant.WaitForExitAsync(
                QualificationConstants.TerminalTimeout,
                cancellationToken).ConfigureAwait(false);
            await outer.ReapAsync(
                QualificationConstants.TerminalTimeout,
                cancellationToken).ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("owner-death-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            helper?.Dispose();
            descendant?.Dispose();
            if (outer is not null)
            {
                if (!outer.IsReaped)
                {
                    try
                    {
                        await outer.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                        artifacts.Log("outer-emergency-cleanup-confirmed");
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.outer.cleanup-incomplete");
                    }
                }
                await outer.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && outer is not null)
                throw new QualificationCleanupException("fixture.outer.cleanup-incomplete");
        }
    }

    private static async Task<ScenarioResultRecord> RunPostCreateCancellationAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var helperId = Guid.NewGuid();
        var correlation = Correlation(helperId);
        var request = new HelperLaunchRequest(correlation, QualificationConstants.Digest);
        var boundary = new WindowsJobBoundary();
        RetainedJobIdentity? job = null;
        RetainedProcessIdentity? process = null;
        IHelperReaper? reaper = null;
        CancelAfterCreateLaunchApi? api = null;
        var cleanupConfirmed = false;
        try
        {
            job = await boundary.CreateAsync(helperId, cancellationToken).ConfigureAwait(false);
            using var postCreateCancellation = new CancellationTokenSource();
            api = new CancelAfterCreateLaunchApi(
                WindowsProcessLaunchApi.Instance,
                postCreateCancellation);
            var specification = new WindowsLaunchSpecification(
                request,
                options.ExecutablePath,
                FixtureOwnedLaunch.BuildCommandLine(
                    options.ExecutablePath,
                    ChildArguments(options, QualificationRole.Helper)));
            var processBoundary = new WindowsProcessBoundary(boundary, api, specification);
            var result = await processBoundary.LaunchSuspendedInJobAsync(
                request,
                job,
                postCreateCancellation.Token).ConfigureAwait(false);
            process = result.Process;
            if (result.Status != HelperLaunchOwnershipStatus.CreatedRejected ||
                process is null || result.Job != job ||
                result.FailureCode != "helper.windows.launch.boundary-canceled" ||
                api.CreateCalls != 1 || api.MetadataCalls != 0 || api.ResumeCalls != 0)
            {
                throw new QualificationAssertionException(
                    "fixture.post-create-cancellation.not-retained");
            }

            Exception? requestFailure = null;
            try
            {
                await processBoundary.TerminateAsync(process, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                requestFailure = exception;
            }
            try
            {
                await boundary.TerminateAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                requestFailure ??= exception;
            }

            reaper = boundary.CreateReaper(job, process);
            await ReapAsync(reaper, process, job, cancellationToken).ConfigureAwait(false);
            cleanupConfirmed = true;
            if (requestFailure is not null)
                throw new QualificationAssertionException("fixture.termination.request-failed");
            artifacts.Log("post-create-cancellation-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            if (!cleanupConfirmed && job is not null)
            {
                try
                {
                    if (api is not null)
                    {
                        try
                        {
                            api.TerminateCapturedProcessExact();
                        }
                        catch
                        {
                            // Independent exact job termination and reaping still follow.
                        }
                        try
                        {
                            api.TerminateCapturedJobExact();
                        }
                        catch
                        {
                            // Exact reaping decides cleanup certainty.
                        }
                    }
                    if (reaper is null)
                    {
                        if (process is not null)
                        {
                            try
                            {
                                var emergency = new WindowsProcessBoundary(
                                    boundary,
                                    WindowsProcessLaunchApi.Instance,
                                    new WindowsLaunchSpecification(
                                        request,
                                        options.ExecutablePath,
                                        options.ExecutablePath));
                                await emergency.TerminateAsync(process, CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                // Independent outer-job termination still follows.
                            }
                        }
                        try
                        {
                            await boundary.TerminateAsync(job, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Exact reaping decides cleanup certainty.
                        }
                        reaper = boundary.CreateReaper(job, process);
                    }
                    await ReapAsync(reaper, process, job, CancellationToken.None)
                        .ConfigureAwait(false);
                    cleanupConfirmed = true;
                }
                catch
                {
                    throw new QualificationCleanupException("fixture.outer.cleanup-incomplete");
                }
            }
            if (cleanupConfirmed)
                await boundary.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ScenarioResultRecord> RunAuthenticatedBootstrapAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? owned = null;
        var cleanupConfirmed = false;
        try
        {
            owned = await FixtureOwnedLaunch.CreateAuthenticatedAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper),
                cancellationToken).ConfigureAwait(false);
            artifacts.Log("bootstrap-child-created", processId: owned.Coordinate.ProcessId);
            await owned.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            var observation = await owned.AuthenticateBootstrapAsync(cancellationToken)
                .ConfigureAwait(false);
            if (observation.Outcome != HelperAuthenticationOutcome.Authenticated ||
                observation.Process != owned.Process ||
                observation.Correlation != owned.Correlation)
            {
                throw new QualificationAssertionException("fixture.bootstrap.observation-mismatch");
            }
            var ready = await artifacts.WaitForJsonAsync(
                "bootstrap-ready.json",
                QualificationJsonContext.Default.BootstrapReadyRecord,
                QualificationConstants.ReadinessTimeout,
                cancellationToken).ConfigureAwait(false);
            if (ready.Nonce != options.Nonce || ready.Role != "helper" ||
                ready.Scenario != QualificationScenario.AuthenticatedBootstrap.ToString() ||
                !ready.Authenticated || !ready.AuthenticatedHelloSent || !ready.SentinelUnavailable)
            {
                throw new QualificationAssertionException("fixture.bootstrap.ready-invalid");
            }
            await owned.TerminateJobExactAsync().ConfigureAwait(false);
            await owned.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("bootstrap-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.IsReaped)
                {
                    try
                    {
                        await owned.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
                    }
                }
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && owned is not null)
                throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
        }
    }

    private static async Task<ScenarioResultRecord> RunRejectedBootstrapMaterialAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? owned = null;
        var cleanupConfirmed = false;
        try
        {
            owned = await FixtureOwnedLaunch.CreateAuthenticatedAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper),
                cancellationToken,
                corruptBootstrapMaterial: true).ConfigureAwait(false);
            artifacts.Log("rejected-bootstrap-child-created", processId: owned.Coordinate.ProcessId);
            await owned.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            var ready = await artifacts.WaitForJsonAsync(
                "bootstrap-ready.json",
                QualificationJsonContext.Default.BootstrapReadyRecord,
                QualificationConstants.ReadinessTimeout,
                cancellationToken).ConfigureAwait(false);
            if (ready.Nonce != options.Nonce || ready.Role != "helper" ||
                ready.Scenario != QualificationScenario.RejectedBootstrapMaterial.ToString() ||
                ready.Authenticated || ready.AuthenticatedHelloSent || !ready.SentinelUnavailable)
            {
                throw new QualificationAssertionException("fixture.bootstrap.rejection-invalid");
            }
            await owned.TerminateJobExactAsync().ConfigureAwait(false);
            await owned.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("rejected-bootstrap-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.IsReaped)
                {
                    try
                    {
                        await owned.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
                    }
                }
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && owned is not null)
                throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
        }
    }

    private static async Task<ScenarioResultRecord> RunRejectedUnintendedPeerAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? intended = null;
        FixtureOwnedLaunch? peer = null;
        var cleanupConfirmed = false;
        try
        {
            intended = await FixtureOwnedLaunch.CreateAuthenticatedAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper),
                cancellationToken).ConfigureAwait(false);
            peer = await intended.CreateUnintendedBootstrapPeerAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper),
                cancellationToken).ConfigureAwait(false);
            artifacts.Log("unintended-peer-created", processId: peer.Coordinate.ProcessId);
            await peer.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await intended.AuthenticateBootstrapAsync(cancellationToken).ConfigureAwait(false);
                throw new QualificationAssertionException("fixture.bootstrap.unintended-peer-accepted");
            }
            catch (HelperBoundaryException exception) when (
                exception.FailureCode == "helper.windows.bootstrap.peer-mismatch")
            {
                artifacts.Log("unintended-peer-rejected", exception.FailureCode);
            }

            await peer.TerminateJobExactAsync().ConfigureAwait(false);
            await peer.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            await intended.TerminateJobExactAsync().ConfigureAwait(false);
            await intended.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            cleanupConfirmed = true;
            artifacts.Log("unintended-peer-terminal");
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            var cleanupFailed = false;
            var allReaped = true;
            foreach (var owned in new[] { peer, intended })
            {
                if (owned is null)
                    continue;
                if (!owned.IsReaped)
                {
                    try
                    {
                        await owned.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        cleanupFailed = true;
                    }
                }
                allReaped &= owned.IsReaped;
                try
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }
            cleanupConfirmed |= allReaped && !cleanupFailed;
            if (cleanupFailed || !cleanupConfirmed && (peer is not null || intended is not null))
                throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
        }
    }

    private static async Task<ScenarioResultRecord> RunRejectedPartialHandshakeAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        FixtureOwnedLaunch? owned = null;
        var cleanupConfirmed = false;
        try
        {
            owned = await FixtureOwnedLaunch.CreateAuthenticatedAsync(
                options.ExecutablePath,
                ChildArguments(options, QualificationRole.Helper),
                cancellationToken).ConfigureAwait(false);
            await owned.ObserveSingletonAndResumeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await owned.AuthenticateBootstrapAsync(cancellationToken).ConfigureAwait(false);
                throw new QualificationAssertionException("fixture.bootstrap.partial-accepted");
            }
            catch (HelperBoundaryException exception) when (
                exception.FailureCode == "helper.windows.bootstrap.transport-broken")
            {
                artifacts.Log("partial-bootstrap-rejected", exception.FailureCode);
            }
            var ready = await artifacts.WaitForJsonAsync(
                "bootstrap-ready.json",
                QualificationJsonContext.Default.BootstrapReadyRecord,
                QualificationConstants.ReadinessTimeout,
                cancellationToken).ConfigureAwait(false);
            if (ready.Nonce != options.Nonce || ready.Role != "helper" ||
                ready.Scenario != QualificationScenario.RejectedPartialHandshake.ToString() ||
                ready.Authenticated || !ready.AuthenticatedHelloSent || !ready.SentinelUnavailable)
            {
                throw new QualificationAssertionException("fixture.bootstrap.rejection-invalid");
            }
            await owned.TerminateJobExactAsync().ConfigureAwait(false);
            await owned.ReapAsync(QualificationConstants.TerminalTimeout, cancellationToken)
                .ConfigureAwait(false);
            cleanupConfirmed = true;
            return new(options.Scenario!.Value.ToString(), true, "passed", 0, true);
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.IsReaped)
                {
                    try
                    {
                        await owned.ForceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                        cleanupConfirmed = true;
                    }
                    catch
                    {
                        throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
                    }
                }
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            if (!cleanupConfirmed && owned is not null)
                throw new QualificationCleanupException("fixture.bootstrap.cleanup-incomplete");
        }
    }

    private static async Task ReapAsync(
        IHelperReaper reaper,
        RetainedProcessIdentity? process,
        RetainedJobIdentity job,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.CleanupTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None)
                .ConfigureAwait(false);
            if (observation.Process == process && observation.Job == job &&
                observation.ProcessExited && observation.JobEmpty)
            {
                await reaper.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationCleanupException("fixture.cleanup.reap-timeout");
    }

    private static async Task<OwnerArmedRecord> WaitForOwnerArmedAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        FixtureOwnedLaunch outer,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.ReadinessTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(artifacts.PathFor("owner-armed.json")))
            {
                return artifacts.ReadJson(
                    "owner-armed.json",
                    QualificationJsonContext.Default.OwnerArmedRecord);
            }
            if (File.Exists(artifacts.PathFor("owner-result.json")))
            {
                var failed = artifacts.ReadJson(
                    "owner-result.json",
                    QualificationJsonContext.Default.OwnerResultRecord);
                RequireCoordinate(options, failed.Nonce, failed.Role, failed.Scenario);
                throw new QualificationAssertionException(
                    failed.Passed ? "fixture.owner.exited-before-arm" : failed.Code);
            }
            if (outer.IsProcessSignaled())
            {
                // Atomic rename means a present result is complete. Check once more after exact
                // owner exit so a final failure record wins over the generic exited code.
                if (File.Exists(artifacts.PathFor("owner-result.json")))
                {
                    var failed = artifacts.ReadJson(
                        "owner-result.json",
                        QualificationJsonContext.Default.OwnerResultRecord);
                    RequireCoordinate(options, failed.Nonce, failed.Role, failed.Scenario);
                    throw new QualificationAssertionException(
                        failed.Passed ? "fixture.owner.exited-before-arm" : failed.Code);
                }
                throw new QualificationAssertionException("fixture.owner.exited-before-arm");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationTimeoutException("fixture.owner.arm-timeout");
    }

    private static IReadOnlyList<string> ChildArguments(
        QualificationOptions options,
        QualificationRole role) =>
        [
            "--opt-in", QualificationConstants.OptInToken,
            "--mode", role.ToString().ToLowerInvariant(),
            "--scenario", options.Scenario!.Value.ToString(),
            "--nonce", options.Nonce.ToString("D"),
            "--artifact-root", options.ArtifactRoot,
            "--executable", options.ExecutablePath,
        ];

    private static void RequireCoordinate(
        QualificationOptions options,
        Guid nonce,
        string role,
        string scenario)
    {
        if (nonce != options.Nonce ||
            !role.Equals("owner", StringComparison.Ordinal) ||
            !scenario.Equals(options.Scenario!.Value.ToString(), StringComparison.Ordinal))
        {
            throw new QualificationAssertionException("fixture.coordination.foreign");
        }
    }

    private static CaptureCorrelation Correlation(Guid helperId) => new(
        Guid.NewGuid(),
        helperId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        1,
        QualificationConstants.Digest);

    private static QualificationExitCode ExitCodeFor(
        IReadOnlyList<ScenarioResultRecord> results,
        int expectedScenarioCount)
    {
        if (results.Any(result => !result.CleanupConfirmed))
            return QualificationExitCode.CleanupIncomplete;
        if (results.All(result => result.Passed) &&
            results.Count == expectedScenarioCount)
        {
            return QualificationExitCode.Pass;
        }
        if (results.Any(result => result.Code.Contains("timeout", StringComparison.Ordinal)))
            return QualificationExitCode.TimeoutCleanupConfirmed;
        if (results.Any(result => result.Code.Contains("infrastructure", StringComparison.Ordinal)))
            return QualificationExitCode.InfrastructureExceptionCleanupConfirmed;
        return QualificationExitCode.AssertionFailedCleanupConfirmed;
    }

    private static string BuildReport(RunResultRecord result)
    {
        var builder = new StringBuilder()
            .AppendLine("# Windows capture-isolation qualification")
            .AppendLine()
            .AppendLine($"Run nonce: `{result.Nonce:D}`")
            .AppendLine($"Result: **{(result.Passed ? "PASS" : "FAIL")}**")
            .AppendLine($"Exit code: `{result.ExitCode}`")
            .AppendLine()
            .AppendLine("| Scenario | Result | Evidence code | Cleanup | Elapsed ms |")
            .AppendLine("|---|---:|---|---:|---:|");
        foreach (var scenario in result.Scenarios)
        {
            builder.Append("| ").Append(scenario.Scenario)
                .Append(" | ").Append(scenario.Passed ? "PASS" : "FAIL")
                .Append(" | `").Append(scenario.Code).Append("` | ")
                .Append(scenario.CleanupConfirmed ? "confirmed" : "uncertain")
                .Append(" | ").Append(scenario.ElapsedMilliseconds).AppendLine(" |");
        }
        builder.AppendLine();
        if (result.Scenarios.Count == 1 &&
            result.Scenarios[0].Scenario == QualificationScenario.AtomicPublicationProbe.ToString())
        {
            builder.AppendLine("This opt-in receipt covers only the fixture-owned legacy")
                .AppendLine("FileRenameInfo same-directory success/collision experiment and recorded")
                .AppendLine("source/binary set. It does not prove that a non-null RootDirectory")
                .AppendLine("controls destination resolution. It does not qualify extended or NT")
                .AppendLine("rename APIs, crash durability, races, root mutation, hard links,")
                .AppendLine("share-mode matrix or")
                .AppendLine("process-kill behavior, production admission, package authenticity,")
                .AppendLine("OBS/UI/devices, packaging, release readiness, or shipment.");
            return builder.ToString();
        }
        if (result.Scenarios.Count == 1 &&
            result.Scenarios[0].Scenario == QualificationScenario.DistinctRootRenameProbe.ToString())
        {
            builder.AppendLine("This opt-in receipt covers only a fixture-owned, same-volume")
                .AppendLine("two-directory legacy FileRenameInfo destination-root experiment.")
                .AppendLine("A failure does not identify its cause. A pass would establish only")
                .AppendLine("destination-handle resolution for this exact environment and rights.")
                .AppendLine("It does not qualify same-directory atomic publication, collision")
                .AppendLine("races, root mutation, crash durability, helper transfer, OBS,")
                .AppendLine("production registration, packaging, release, or shipment.");
            return builder.ToString();
        }
        builder.AppendLine("This receipt qualifies only this run's tested Windows nested-job environment")
            .AppendLine("and recorded headless Windows-subsystem fixture/binary set, including the")
            .AppendLine("bounded unregistered authenticated-bootstrap scenarios plus create-new")
            .AppendLine("handle-relative media-file and real junction-mutation containment scenarios.")
            .AppendLine("It does not qualify a console helper, a production subsystem decision, package")
            .AppendLine("authenticity, production registration, media-root evidence, mapped-network roots,")
            .AppendLine("atomic media publication, OBS/UI/device behavior,")
            .AppendLine("production timing, packaging, release readiness, or shipment.");
        return builder.ToString();
    }

    private static string ManifestHash(string value, int expectedLength) =>
        value.Equals("unavailable", StringComparison.Ordinal) ||
        value.Length == expectedLength && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            ? value.ToLowerInvariant()
            : "unavailable";

    private static FixtureBinaryEvidence CaptureBinaryEvidence(QualificationOptions options)
    {
        var exactAppHost = Path.GetFullPath(options.ExecutablePath);
        var outputDirectory = Path.GetDirectoryName(exactAppHost);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !File.Exists(exactAppHost))
            throw new FixturePrerequisiteException("fixture.binary.apphost-missing");

        Subsystem subsystem;
        try
        {
            using var appHost = new FileStream(
                exactAppHost,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var peReader = new PEReader(appHost);
            subsystem = peReader.PEHeaders.PEHeader?.Subsystem ??
                throw new FixturePrerequisiteException("fixture.binary.pe-header-missing");
        }
        catch (BadImageFormatException)
        {
            throw new FixturePrerequisiteException("fixture.binary.pe-invalid");
        }

        var qualification = RequireLoadedOutputAssembly(
            outputDirectory,
            "WatchDog.CaptureIsolation.Windows.Qualification.dll",
            typeof(QualificationRunner).Assembly,
            options.QualificationAssemblyHash);
        var windows = RequireLoadedOutputAssembly(
            outputDirectory,
            "WatchDog.CaptureIsolation.Windows.dll",
            typeof(WindowsProcessLaunchApi).Assembly,
            options.CaptureIsolationWindowsHash);
        var isolation = RequireLoadedOutputAssembly(
            outputDirectory,
            "WatchDog.CaptureIsolation.dll",
            typeof(HelperLaunchRequest).Assembly,
            options.CaptureIsolationHash);
        var protocol = RequireLoadedOutputAssembly(
            outputDirectory,
            "WatchDog.CaptureProtocol.dll",
            typeof(CaptureCorrelation).Assembly,
            options.CaptureProtocolHash);
        return new(
            subsystem,
            HashFile(exactAppHost),
            qualification.Sha256,
            windows.Sha256,
            isolation.Sha256,
            protocol.Sha256,
            qualification.Mvid,
            windows.Mvid,
            isolation.Mvid,
            protocol.Mvid);
    }

    private static FixtureAssemblyAttribution RequireLoadedOutputAssembly(
        string outputDirectory,
        string fileName,
        System.Reflection.Assembly loadedAssembly,
        string expectedSha256)
    {
        return FixtureBinaryAttribution.ObserveLoaded(
            outputDirectory, fileName, loadedAssembly, expectedSha256) ??
            throw new FixturePrerequisiteException("fixture.binary.loaded-output-mismatch");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record FixtureBinaryEvidence(
        Subsystem Subsystem,
        string AppHostSha256,
        string QualificationAssemblySha256,
        string CaptureIsolationWindowsSha256,
        string CaptureIsolationSha256,
        string CaptureProtocolSha256,
        Guid QualificationAssemblyMvid,
        Guid CaptureIsolationWindowsMvid,
        Guid CaptureIsolationMvid,
        Guid CaptureProtocolMvid);

    private sealed class FixturePrerequisiteException(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    private sealed class CancelAfterCreateLaunchApi(
        IWindowsProcessLaunchApi inner,
        CancellationTokenSource cancellation) : IWindowsProcessLaunchApi
    {
        private SafeWindowsHandle? _capturedJob;
        private SafeWindowsHandle? _capturedProcess;
        internal int CreateCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal int ResumeCalls { get; private set; }

        public void CreateSuspendedInJob(
            WindowsLaunchSpecification specification,
            SafeWindowsHandle jobHandle,
            IWindowsProcessOwnershipSink ownershipSink,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            _capturedJob = jobHandle;
            inner.CreateSuspendedInJob(
                specification,
                jobHandle,
                new ForwardingOwnershipSink(ownershipSink, this),
                cancellationToken);
            cancellation.Cancel();
        }

        public uint GetProcessId(SafeWindowsHandle processHandle)
        {
            MetadataCalls++;
            return inner.GetProcessId(processHandle);
        }

        public uint GetThreadId(SafeWindowsHandle threadHandle)
        {
            MetadataCalls++;
            return inner.GetThreadId(threadHandle);
        }

        public long GetProcessCreationFileTime(SafeWindowsHandle processHandle)
        {
            MetadataCalls++;
            return inner.GetProcessCreationFileTime(processHandle);
        }

        public uint ResumePrimaryThread(SafeWindowsHandle threadHandle)
        {
            ResumeCalls++;
            return inner.ResumePrimaryThread(threadHandle);
        }

        public void TerminateProcess(SafeWindowsHandle processHandle) =>
            inner.TerminateProcess(processHandle);

        internal void TerminateCapturedProcessExact()
        {
            if (_capturedProcess is not null)
                inner.TerminateProcess(_capturedProcess);
        }

        internal void TerminateCapturedJobExact()
        {
            if (_capturedJob is not null)
                WindowsContainmentApi.Instance.TerminateJob(_capturedJob);
        }

        private sealed class ForwardingOwnershipSink(
            IWindowsProcessOwnershipSink innerSink,
            CancelAfterCreateLaunchApi owner) : IWindowsProcessOwnershipSink
        {
            public void CaptureCreatedProcess(
                SafeWindowsHandle processHandle,
                SafeWindowsHandle primaryThreadHandle,
                uint processId,
                uint primaryThreadId)
            {
                innerSink.CaptureCreatedProcess(
                    processHandle,
                    primaryThreadHandle,
                    processId,
                    primaryThreadId);
                owner._capturedProcess = processHandle;
            }
        }
    }
}
