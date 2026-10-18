using WatchDog.Core.Recording;
using WatchDog.Core.Devices;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Recording;

public sealed class ObsWebcamRecorderTests
{
    private const string ExactDeviceId = "opaque:camera:id:#22value";
    private const string ExactDisplayName = "Exact Camera";

    [Fact]
    public async Task CatalogValidationHappensBeforeNativeSourceCreation()
    {
        var enumerator = new StubEnumerator(
        [
            new WebcamDeviceInfo(ExactDeviceId, ExactDisplayName),
            new WebcamDeviceInfo(ExactDeviceId, "Duplicate"),
        ]);
        var factory = new FakeNativeSessionFactory(new FakeNativeSession());
        using var recorder = new ObsWebcamRecorder(enumerator, factory);

        var error = await Assert.ThrowsAsync<CaptureDeviceStartupException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        Assert.Equal(CaptureDeviceStartupStage.Catalog, error.Stage);
        Assert.Equal(CaptureDeviceStartupReason.InvalidCatalog, error.Reason);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(recorder.HasActiveResources);
    }

    [Fact]
    public async Task MissingExactSelectionIsDistinctFromCatalogFailureAndCreatesNoNativeSource()
    {
        var factory = new FakeNativeSessionFactory(new FakeNativeSession());
        using var recorder = new ObsWebcamRecorder(
            new StubEnumerator([new WebcamDeviceInfo("other-device", "Other Camera")]),
            factory);

        var error = await Assert.ThrowsAsync<CaptureDeviceStartupException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        Assert.Equal(CaptureDeviceRole.Webcam, error.Role);
        Assert.Equal(CaptureDeviceStartupStage.Selection, error.Stage);
        Assert.Equal(CaptureDeviceStartupReason.ExactSelectionUnavailable, error.Reason);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task FriendlyDisplayNameChangeDoesNotReplaceExactOpaqueIdentity()
    {
        var enumerator = new StubEnumerator(
            [new WebcamDeviceInfo(ExactDeviceId, "Renamed Camera")]);
        var nativeSession = new FakeNativeSession();
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = new ObsWebcamRecorder(enumerator, factory);

        await recorder.StartAsync(CreateSelection(), CreateOutputPath());

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(ExactDeviceId, nativeSession.Intent?.DeviceId);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task StartFreezesExactVideoOnlyIntent()
    {
        var nativeSession = new FakeNativeSession();
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);
        var outputPath = CreateOutputPath("camera.mp4");

        await recorder.StartAsync(CreateSelection(), outputPath);

        var intent = Assert.IsType<ObsWebcamRecordingIntent>(nativeSession.Intent);
        Assert.Equal(ExactDeviceId, intent.DeviceId);
        Assert.Equal(ExactDisplayName, intent.DisplayName);
        Assert.Equal(1280, intent.Width);
        Assert.Equal(720, intent.Height);
        Assert.Equal(30, intent.FramesPerSecond);
        Assert.Equal("MJPEG", intent.VideoFormat);
        Assert.Equal(1_500, intent.VideoBitrateKbps);
        Assert.Equal(Path.GetFullPath(outputPath), intent.OutputPath);
        Assert.NotEqual(intent.OutputPath, intent.NativeOutputPath);
        Assert.Equal(Path.GetDirectoryName(intent.OutputPath), Path.GetDirectoryName(intent.NativeOutputPath));
        Assert.Equal(Path.GetExtension(intent.OutputPath), Path.GetExtension(intent.NativeOutputPath));
        Assert.True(intent.AttachMuxerCompatibilityAudioEncoder);
        Assert.False(intent.FinalMediaContainsAudio);
        Assert.Equal(1, nativeSession.StartCalls);
        Assert.True(recorder.IsRecording);
        Assert.True(recorder.HasActiveResources);

        await recorder.StopAsync();

        Assert.False(recorder.IsRecording);
        Assert.False(recorder.HasActiveResources);
        Assert.Equal(1, nativeSession.CleanupCalls);
    }

    [Fact]
    public async Task FailedPartialStartIsCleanedBeforeErrorReturns()
    {
        var nativeSession = new FakeNativeSession { StartResult = false };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        var error = await Assert.ThrowsAsync<CaptureDeviceStartupException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        Assert.Equal(CaptureDeviceStartupStage.Start, error.Stage);
        Assert.Equal(CaptureDeviceStartupReason.Unknown, error.Reason);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(1, nativeSession.CleanupCalls);
        Assert.False(nativeSession.HasActiveResources);
        Assert.False(recorder.HasActiveResources);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task ReadinessFailureAfterOutputStartCleansUpBeforeRecordingIsPublished()
    {
        var nativeSession = new FakeNativeSession
        {
            StartFailure = WebcamReadinessFailure(),
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        var error = await Assert.ThrowsAsync<CaptureDeviceStartupException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        Assert.Equal(CaptureDeviceStartupStage.Readiness, error.Stage);
        Assert.Equal(CaptureDeviceStartupReason.Unknown, error.Reason);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Equal(1, nativeSession.StartCalls);
        Assert.Equal(1, nativeSession.CleanupCalls);
        Assert.False(nativeSession.HasActiveResources);
        Assert.False(recorder.HasActiveResources);
        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.CurrentOutputPath);
    }

    [Fact]
    public async Task CancellationDuringConfigureDoesNotStartNativeOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var nativeSession = new FakeNativeSession
        {
            ConfigureCompleted = cancellation.Cancel,
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath(), cancellation.Token));

        Assert.Equal(0, nativeSession.StartCalls);
        Assert.Equal(1, nativeSession.CleanupCalls);
        Assert.False(nativeSession.HasActiveResources);
        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.CurrentOutputPath);
    }

    [Fact]
    public async Task StartAsyncYieldsWhileNativeReadinessIsPending()
    {
        using var releaseStart = new ManualResetEventSlim();
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeSession = new FakeNativeSession
        {
            StartEntered = startEntered,
            ReleaseStart = releaseStart,
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        var start = recorder.StartAsync(CreateSelection(), CreateOutputPath());
        try
        {
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(start.IsCompleted);
            Assert.False(recorder.IsRecording);
        }
        finally
        {
            releaseStart.Set();
            await start;
        }

        await recorder.StopAsync();
    }

    [Fact]
    public void FailedReadinessDoesNotArmFinalizedMediaValidation()
    {
        var validationArmed = false;

        var error = Assert.Throws<TimeoutException>(() =>
            ObsWebcamNativeSession.ArmFinalizedMediaValidationAfterReadiness(
                () => throw new TimeoutException("exact dimensions were not ready"),
                () => validationArmed = true));

        Assert.Equal("exact dimensions were not ready", error.Message);
        Assert.False(validationArmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedFirstViewDisposalReleasesReferenceWithoutLatchingClearFailure(bool clearFails)
    {
        var originalView = new object();
        object? retainedView = originalView;
        Exception? releaseFailure = null;
        var clearFailure = new IOException("fixture channel clear failed");
        var calls = new List<string>();
        var failures = new List<Exception>();

        ObsWebcamNativeSession.ReleaseView(
            ref retainedView,
            ref releaseFailure,
            view =>
            {
                Assert.Same(originalView, view);
                calls.Add("clear");
                if (clearFails)
                    throw clearFailure;
            },
            view =>
            {
                Assert.Same(originalView, view);
                calls.Add("dispose");
            },
            failures);

        Assert.Null(retainedView);
        Assert.Null(releaseFailure);
        Assert.Equal(new[] { "clear", "dispose" }, calls);
        if (clearFails)
            Assert.Same(clearFailure, Assert.Single(failures));
        else
            Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedViewDisposalCannotBeResolvedByDisposedWrapperNoOp(
        bool clearFails,
        bool nativeActionCompleted)
    {
        var originalView = new object();
        object? retainedView = originalView;
        Exception? releaseFailure = null;
        var clearFailure = new IOException("fixture channel clear failed");
        var disposeFailure = new IOException("fixture disposal failed");
        var disposed = false;
        var completedWork = false;
        var clearCalls = 0;
        var disposeCalls = 0;
        var failures = new List<Exception>();
        Action<object> clearSource = view =>
        {
            clearCalls++;
            Assert.Same(originalView, view);
            ObjectDisposedException.ThrowIf(disposed, view);
            if (clearFails)
                throw clearFailure;
        };
        Action<object> dispose = view =>
        {
            disposeCalls++;
            Assert.Same(originalView, view);
            // Model the inspected wrapper setting disposed before fallible native work.
            if (disposed)
                return;
            disposed = true;
            completedWork = nativeActionCompleted;
            throw disposeFailure;
        };

        ObsWebcamNativeSession.ReleaseView(
            ref retainedView, ref releaseFailure, clearSource, dispose, failures);

        Assert.Same(originalView, retainedView);
        Assert.Equal(nativeActionCompleted, completedWork);
        var initialFailure = Assert.Single(failures);
        if (clearFails)
        {
            var aggregate = Assert.IsType<AggregateException>(initialFailure);
            Assert.Collection(aggregate.InnerExceptions,
                error => Assert.Same(clearFailure, error),
                error => Assert.Same(disposeFailure, error));
        }
        else
        {
            Assert.Same(disposeFailure, initialFailure);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            failures.Clear();
            ObsWebcamNativeSession.ReleaseView(
                ref retainedView, ref releaseFailure, clearSource, dispose, failures);
            ObsWebcamNativeSession.ReleaseView(
                ref retainedView, ref releaseFailure, clearSource, dispose, failures);

            Assert.Same(originalView, retainedView);
            Assert.Same(initialFailure, releaseFailure);
            Assert.Same(initialFailure, Assert.Single(failures));
            Assert.Equal(1, clearCalls);
            Assert.Equal(1, disposeCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrSubstitutedViewCannotResolveRetainedDisposalFailure(bool substituteView)
    {
        object? retainedView = substituteView ? new object() : null;
        var suppliedView = retainedView;
        var originalFailure = new IOException("fixture unresolved disposal");
        Exception? releaseFailure = originalFailure;
        var calls = 0;
        var failures = new List<Exception>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            failures.Clear();
            ObsWebcamNativeSession.ReleaseView(
                ref retainedView, ref releaseFailure, _ => calls++, _ => calls++, failures);

            // This guard cannot recover an original reference already lost by a caller.
            Assert.Same(suppliedView, retainedView);
            Assert.Same(originalFailure, releaseFailure);
            Assert.Same(originalFailure, Assert.Single(failures));
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void ViewCleanupWithoutReferenceOrUncertaintyDoesNoWork()
    {
        object? retainedView = null;
        Exception? releaseFailure = null;
        var calls = 0;
        var failures = new List<Exception>();

        ObsWebcamNativeSession.ReleaseView(
            ref retainedView, ref releaseFailure, _ => calls++, _ => calls++, failures);

        Assert.Equal(0, calls);
        Assert.Empty(failures);
        Assert.Null(releaseFailure);
    }

    [Fact]
    public async Task ReadinessFailureWithIncompleteCleanupRetainsOwnershipForRetry()
    {
        var nativeSession = new FakeNativeSession
        {
            StartFailure = WebcamReadinessFailure(),
            CleanupFailuresRemaining = 1,
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        var deviceFailure = CaptureDeviceStartupException.Find(error);
        Assert.NotNull(deviceFailure);
        Assert.Equal(CaptureDeviceStartupStage.Readiness, deviceFailure.Stage);
        Assert.False(recorder.IsRecording);
        Assert.True(recorder.HasActiveResources);
        Assert.Equal(1, nativeSession.CleanupCalls);

        await recorder.StopAsync();

        Assert.Equal(2, nativeSession.CleanupCalls);
        Assert.False(recorder.HasActiveResources);
    }

    [Fact]
    public async Task FailedStartCleanupRetainsNativeSessionForRetry()
    {
        var nativeSession = new FakeNativeSession
        {
            ConfigureFailure = new InvalidOperationException("configure failed"),
            CleanupFailuresRemaining = 1,
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));

        var deviceFailure = CaptureDeviceStartupException.Find(error);
        Assert.NotNull(deviceFailure);
        Assert.Equal(CaptureDeviceStartupStage.Binding, deviceFailure.Stage);
        Assert.Equal(CaptureDeviceStartupReason.Unknown, deviceFailure.Reason);
        Assert.True(recorder.HasActiveResources);
        Assert.Equal(1, nativeSession.CleanupCalls);

        await recorder.StopAsync();

        Assert.Equal(2, nativeSession.CleanupCalls);
        Assert.False(recorder.HasActiveResources);
    }

    [Fact]
    public async Task FailedStopCleanupRetainsNativeSessionForRetry()
    {
        var nativeSession = new FakeNativeSession { CleanupFailuresRemaining = 1 };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);
        await recorder.StartAsync(CreateSelection(), CreateOutputPath());

        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());

        Assert.False(recorder.IsRecording);
        Assert.True(recorder.HasActiveResources);

        await recorder.StopAsync();

        Assert.Equal(2, nativeSession.CleanupCalls);
        Assert.False(recorder.HasActiveResources);
    }

    [Fact]
    public async Task UncertainViewReleaseIsDerivedFromRetainedNativeSessionOnly()
    {
        var nativeSession = new FakeNativeSession { CleanupFailuresRemaining = 1 };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);
        Assert.False(recorder.HasUncertainViewRelease);
        await recorder.StartAsync(CreateSelection(), CreateOutputPath());
        nativeSession.HasUncertainViewRelease = true;

        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());

        Assert.True(recorder.HasUncertainViewRelease);
        var cleanupCalls = nativeSession.CleanupCalls;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.StopAsync(cancellation.Token));
        Assert.True(recorder.HasUncertainViewRelease);
        Assert.Equal(cleanupCalls, nativeSession.CleanupCalls);
        nativeSession.HasUncertainViewRelease = false;
        await recorder.StopAsync();
        Assert.False(recorder.HasUncertainViewRelease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentNativeCleanupFailureRetainsOuterSessionAndBlocksAdmission(bool startFails)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            // This whole-session fake tests the outer owner, not the View release helper.
            var outputPath = Path.Combine(directory, "camera.mkv");
            var originalPath = Path.Combine(directory, "intermediate.mkv");
            byte[] originalBytes = [1, 2, 3];
            File.WriteAllBytes(originalPath, originalBytes);
            var finalizer = new FakeVideoOnlyFinalizer((source, target) => File.Copy(source, target));
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(originalPath, outputPath);
            var releaseFailures = new List<Exception>();
            work.ObserveOutputRelease(validateFinalizedMedia: true, hasEncodedVideoFrames: true, releaseFailures);
            Assert.Empty(releaseFailures);
            var nativeSession = new FakeNativeSession
            {
                CleanupFailuresRemaining = int.MaxValue,
                StartFailure = startFails ? WebcamReadinessFailure() : null,
                PendingFileWork = work,
            };
            var factory = new FakeNativeSessionFactory(nativeSession);
            var recorder = CreateRecorder(factory);
            if (startFails)
                await Assert.ThrowsAsync<AggregateException>(() => recorder.StartAsync(CreateSelection(), outputPath));
            else
                await recorder.StartAsync(CreateSelection(), outputPath);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());
                Assert.Throws<IOException>(() => recorder.Dispose());

                Assert.True(recorder.HasNativeResources);
                Assert.True(recorder.HasActiveResources);
                Assert.False(recorder.IsRecording);
                Assert.Equal(startFails ? null : outputPath, recorder.CurrentOutputPath);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recorder.StartAsync(CreateSelection(), outputPath));
                Assert.Equal(1, factory.CreateCalls);
                Assert.Equal(1, nativeSession.StartCalls);
                Assert.Equal(outputPath, nativeSession.Intent!.OutputPath);
                Assert.True(work.HasPendingWork);
                Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
                Assert.False(File.Exists(outputPath));
                Assert.Null(finalizer.Call);
            }

            Assert.Equal(6 + (startFails ? 1 : 0), nativeSession.CleanupCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PendingFileFinalizationRetainsAdmissionWithoutClaimingNativeOwnership()
    {
        var nativeSession = new FakeNativeSession
        {
            CleanupFailuresRemaining = 1,
            ReleaseNativeResourcesBeforeCleanupFailure = true,
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);
        await recorder.StartAsync(CreateSelection(), CreateOutputPath());

        await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());

        Assert.True(recorder.HasActiveResources);
        Assert.False(recorder.HasNativeResources);
        Assert.NotNull(recorder.CurrentOutputPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.StartAsync(CreateSelection(), CreateOutputPath()));
        Assert.Equal(1, factory.CreateCalls);

        await recorder.StopAsync();

        Assert.False(recorder.HasActiveResources);
        Assert.False(recorder.HasNativeResources);
        Assert.Null(recorder.CurrentOutputPath);
    }

    [Fact]
    public async Task ReleasedStopFailureClearsStalePublicOutputPath()
    {
        var nativeSession = new FakeNativeSession
        {
            CleanupFailureAfterRelease = new IOException("finalized media was invalid"),
        };
        var factory = new FakeNativeSessionFactory(nativeSession);
        using var recorder = CreateRecorder(factory);
        await recorder.StartAsync(CreateSelection(), CreateOutputPath());

        var error = await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());

        Assert.Equal("finalized media was invalid", error.Message);
        Assert.False(recorder.IsRecording);
        Assert.False(recorder.HasActiveResources);
        Assert.Null(recorder.CurrentOutputPath);
    }

    [Fact]
    public void FinalizedMediaMustExistAndBeNonEmpty()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WatchDog-webcam-media-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var missing = Path.Combine(directory, "missing.mkv");
            var empty = Path.Combine(directory, "empty.mkv");
            var valid = Path.Combine(directory, "valid.mkv");
            File.WriteAllBytes(empty, []);
            File.WriteAllBytes(valid, [1]);

            Assert.Throws<IOException>(() => ObsWebcamNativeSession.ValidateFinalizedMedia(null));
            Assert.Throws<IOException>(() => ObsWebcamNativeSession.ValidateFinalizedMedia(missing));
            Assert.Throws<IOException>(() => ObsWebcamNativeSession.ValidateFinalizedMedia(empty));
            ObsWebcamNativeSession.ValidateFinalizedMedia(valid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VideoOnlyFinalizationDeletesIntermediateOnlyAfterSuccess()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var intermediate = Path.Combine(directory, "camera.watchdog-mux.mkv");
            var final = Path.Combine(directory, "camera.mkv");
            File.WriteAllBytes(intermediate, [1, 2, 3]);
            var finalizer = new FakeVideoOnlyFinalizer((source, destination) =>
                File.Copy(source, destination));

            ObsWebcamNativeSession.FinalizeVideoOnlyMedia(finalizer, intermediate, final);

            Assert.Equal((intermediate, final), finalizer.Call);
            Assert.False(File.Exists(intermediate));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(final));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VideoOnlyFinalizationFailureRetainsIntermediateForRetry()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var intermediate = Path.Combine(directory, "camera.watchdog-mux.mkv");
            var final = Path.Combine(directory, "camera.mkv");
            File.WriteAllBytes(intermediate, [1, 2, 3]);
            var finalizer = new FakeVideoOnlyFinalizer((_, _) =>
                throw new IOException("remux failed"));

            var error = Assert.Throws<IOException>(() =>
                ObsWebcamNativeSession.FinalizeVideoOnlyMedia(finalizer, intermediate, final));

            Assert.Equal("remux failed", error.Message);
            Assert.True(File.Exists(intermediate));
            Assert.False(File.Exists(final));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\"}]}", true)]
    [InlineData("{\"streams\":[]}", false)]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\"},{\"codec_type\":\"video\"}]}", false)]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\"},{\"codec_type\":\"audio\"}]}", false)]
    public void FinalMediaProbeRequiresExactlyOneVideoAndZeroAudio(string probeJson, bool valid)
    {
        if (valid)
        {
            FFmpegWebcamVideoOnlyFinalizer.ValidateProbeJson(probeJson);
            return;
        }

        Assert.Throws<InvalidDataException>(() =>
            FFmpegWebcamVideoOnlyFinalizer.ValidateProbeJson(probeJson));
    }

    [Fact]
    public void VideoOnlyRemuxArgumentsCopyVideoAndExplicitlyDropAudio()
    {
        var arguments = FFmpegWebcamVideoOnlyFinalizer.BuildFfmpegArguments(
            "input.mkv",
            "output.mkv");

        Assert.Contains("0:v:0", arguments);
        Assert.Contains("copy", arguments);
        Assert.Contains("-an", arguments);
        Assert.DoesNotContain("0:a", arguments);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void PostReadinessValidationFailureRetainsFileCustodyAcrossCleanup(int originalLength)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = Path.Combine(directory, "intermediate.mkv");
            var destination = Path.Combine(directory, "final.mkv");
            byte[] bytes = originalLength < 0 ? [] : new byte[originalLength];
            if (originalLength >= 0)
                File.WriteAllBytes(original, bytes);
            var finalizer = new FakeVideoOnlyFinalizer((_, _) => throw new InvalidOperationException());
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(original, destination);
            var failures = new List<Exception>();

            work.ObserveOutputRelease(validateFinalizedMedia: true, hasEncodedVideoFrames: originalLength <= 0, failures);
            var failure = Assert.IsType<IOException>(Assert.Single(failures));
            Assert.Equal(originalLength <= 0
                ? "Webcam output finalized without a non-empty media file."
                : "Webcam output finalized without any encoded video frames.", failure.Message);
            work.Complete(failures);
            Assert.Same(failure, Assert.Single(failures));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var repeatedFailures = new List<Exception>();
                work.Complete(repeatedFailures);
                Assert.Same(failure, Assert.Single(repeatedFailures));
                Assert.True(work.HasPendingWork);
                Assert.Equal(originalLength >= 0, File.Exists(original));
                if (originalLength >= 0)
                    Assert.Equal(bytes, File.ReadAllBytes(original));
                Assert.False(File.Exists(destination));
                Assert.Null(finalizer.Call);
            }

            // A file appearing or changing later does not authorize publishing a failed capture.
            File.WriteAllBytes(original, [9, 8, 7]);
            failures.Clear();
            work.Complete(failures);
            Assert.Same(failure, Assert.Single(failures));
            Assert.Null(finalizer.Call);
            Assert.True(work.HasPendingWork);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreReadinessFailureStillDiscardsIntermediate(bool canceled)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = Path.Combine(directory, "intermediate.mkv");
            var destination = Path.Combine(directory, "final.mkv");
            File.WriteAllBytes(original, [1, 2, 3]);
            var finalizer = new FakeVideoOnlyFinalizer((_, _) => throw new InvalidOperationException());
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(original, destination);
            var armed = false;
            Assert.ThrowsAny<Exception>(() => ObsWebcamNativeSession.ArmFinalizedMediaValidationAfterReadiness(
                () => throw (canceled ? new OperationCanceledException() : new TimeoutException()),
                () => armed = true));
            var failures = new List<Exception>();

            work.ObserveOutputRelease(armed, hasEncodedVideoFrames: true, failures);
            work.Complete(failures);
            work.Complete(failures);

            Assert.Empty(failures);
            Assert.False(work.HasPendingWork);
            Assert.False(File.Exists(original));
            Assert.False(File.Exists(destination));
            Assert.Null(finalizer.Call);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PendingFinalizationRetriesFinalizerFailureBeforeDeletingOriginal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = Path.Combine(directory, "intermediate.mkv");
            var destination = Path.Combine(directory, "final.mkv");
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(original, bytes);
            var attempts = 0;
            var finalizer = new FakeVideoOnlyFinalizer((source, target) =>
            {
                if (++attempts == 1)
                    throw new IOException("fixture finalizer failed");
                File.Copy(source, target);
            });
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(original, destination);
            var failures = new List<Exception>();

            work.ObserveOutputRelease(validateFinalizedMedia: true, hasEncodedVideoFrames: true, failures);
            Assert.Empty(failures);
            work.Complete(failures);

            Assert.IsType<IOException>(Assert.Single(failures));
            Assert.True(work.HasPendingWork);
            Assert.Equal(bytes, File.ReadAllBytes(original));
            Assert.False(File.Exists(destination));

            failures.Clear();
            work.Complete(failures);
            work.Complete(failures);

            Assert.Empty(failures);
            Assert.Equal(2, attempts);
            Assert.False(work.HasPendingWork);
            Assert.False(File.Exists(original));
            Assert.Equal(bytes, File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingDeletionRetainsCustodyAndDoesNotRepeatPublication(bool validationArmed)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = Path.Combine(directory, "intermediate.mkv");
            var destination = Path.Combine(directory, "final.mkv");
            File.WriteAllBytes(original, [1, 2, 3]);
            var calls = 0;
            var finalizer = new FakeVideoOnlyFinalizer((source, target) =>
            {
                calls++;
                File.Copy(source, target);
            });
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(original, destination);
            var failures = new List<Exception>();
            work.ObserveOutputRelease(validationArmed, hasEncodedVideoFrames: true, failures);

            using (var lockedOriginal = File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                work.Complete(failures);
                Assert.IsType<IOException>(Assert.Single(failures));
                Assert.True(work.HasPendingWork);
                Assert.True(File.Exists(original));
                Assert.Equal(validationArmed, File.Exists(destination));
            }

            failures.Clear();
            work.Complete(failures);
            work.Complete(failures);

            Assert.Empty(failures);
            Assert.False(work.HasPendingWork);
            Assert.False(File.Exists(original));
            Assert.Equal(validationArmed ? 1 : 0, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedValidationFailureBlocksAdmissionAfterRepeatedStopAndDispose()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = Path.Combine(directory, "intermediate.mkv");
            var destination = Path.Combine(directory, "final.mkv");
            File.WriteAllBytes(original, [1, 2, 3]);
            var finalizer = new FakeVideoOnlyFinalizer((_, _) => throw new InvalidOperationException());
            var work = new ObsWebcamNativeSession.FileWork(finalizer);
            work.SetPaths(original, destination);
            var nativeSession = new FakeNativeSession { PendingFileWork = work, CleanupFailuresRemaining = 1 };
            var factory = new FakeNativeSessionFactory(nativeSession);
            var recorder = CreateRecorder(factory);
            await recorder.StartAsync(CreateSelection(), destination);
            var releaseFailures = new List<Exception>();
            work.ObserveOutputRelease(validateFinalizedMedia: true, hasEncodedVideoFrames: false, releaseFailures);
            Assert.Single(releaseFailures);

            await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());
            Assert.True(recorder.HasNativeResources);
            Assert.True(recorder.HasActiveResources);
            Assert.True(File.Exists(original));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAsync<IOException>(() => recorder.StopAsync());
                Assert.False(recorder.HasNativeResources);
                Assert.True(recorder.HasActiveResources);
                Assert.False(recorder.IsRecording);
                Assert.Equal(destination, recorder.CurrentOutputPath);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    recorder.StartAsync(CreateSelection(), destination));
            }

            Assert.Throws<IOException>(() => recorder.Dispose());
            Assert.True(recorder.HasActiveResources);
            Assert.Equal(destination, recorder.CurrentOutputPath);
            Assert.Equal(1, factory.CreateCalls);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
            Assert.False(File.Exists(destination));
            Assert.Null(finalizer.Call);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ObsWebcamRecorder CreateRecorder(FakeNativeSessionFactory factory) =>
        new(
            new StubEnumerator([new WebcamDeviceInfo(ExactDeviceId, ExactDisplayName)]),
            factory);

    private static CaptureDeviceStartupException WebcamReadinessFailure() =>
        new(
            CaptureDeviceRole.Webcam,
            CaptureDeviceStartupStage.Readiness,
            CaptureDeviceStartupReason.Unknown,
            "The selected webcam did not become ready for capture.",
            new TimeoutException("exact dimensions were not ready"));

    private static WebcamCaptureSelection CreateSelection() =>
        new(
            ExactDeviceId,
            ExactDisplayName,
            width: 1280,
            height: 720,
            framesPerSecond: 30,
            videoFormat: "MJPEG",
            videoBitrateKbps: 1_500);

    private static string CreateOutputPath(string fileName = "camera.mkv") =>
        Path.Combine(Path.GetTempPath(), "WatchDog-webcam-tests", Guid.NewGuid().ToString("N"), fileName);

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WatchDog-webcam-media-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubEnumerator(IReadOnlyList<WebcamDeviceInfo> devices) : IWebcamDeviceEnumerator
    {
        public IReadOnlyList<WebcamDeviceInfo> GetPhysicalDevicesOrThrow() => devices;
    }

    private sealed class FakeNativeSessionFactory(FakeNativeSession session) : IObsWebcamNativeSessionFactory
    {
        public int CreateCalls { get; private set; }

        public IObsWebcamNativeSession Create()
        {
            CreateCalls++;
            return session;
        }
    }

    private sealed class FakeNativeSession : IObsWebcamNativeSession
    {
        private bool _hasActiveResources = true;
        public bool HasActiveResources => _hasActiveResources || PendingFileWork?.HasPendingWork == true;
        public ObsWebcamNativeSession.FileWork? PendingFileWork { get; init; }
        public bool HasNativeResources { get; private set; } = true;
        public bool HasUncertainViewRelease { get; set; }
        public string? LastError { get; init; }
        public ObsWebcamRecordingIntent? Intent { get; private set; }
        public Exception? ConfigureFailure { get; init; }
        public Action? ConfigureCompleted { get; init; }
        public bool StartResult { get; init; } = true;
        public Exception? StartFailure { get; init; }
        public TaskCompletionSource<bool>? StartEntered { get; init; }
        public ManualResetEventSlim? ReleaseStart { get; init; }
        public int StartCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public int CleanupFailuresRemaining { get; set; }
        public bool ReleaseNativeResourcesBeforeCleanupFailure { get; init; }
        public Exception? CleanupFailureAfterRelease { get; init; }

        public void Configure(ObsWebcamRecordingIntent intent)
        {
            Intent = intent;
            if (ConfigureFailure is not null)
                throw ConfigureFailure;
            ConfigureCompleted?.Invoke();
        }

        public bool Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            StartEntered?.TrySetResult(true);
            ReleaseStart?.Wait(cancellationToken);
            if (StartFailure is not null)
                throw StartFailure;
            return StartResult;
        }

        public void Cleanup()
        {
            CleanupCalls++;
            if (CleanupFailuresRemaining > 0)
            {
                CleanupFailuresRemaining--;
                if (ReleaseNativeResourcesBeforeCleanupFailure)
                    HasNativeResources = false;
                throw new IOException("cleanup failed");
            }

            _hasActiveResources = false;
            HasNativeResources = false;
            if (PendingFileWork is not null)
            {
                var failures = new List<Exception>();
                PendingFileWork.Complete(failures);
                if (failures.Count > 0)
                    throw Assert.Single(failures);
            }
            if (CleanupFailureAfterRelease is not null)
                throw CleanupFailureAfterRelease;
        }
    }

    private sealed class FakeVideoOnlyFinalizer(Action<string, string> finalize) : IWebcamVideoOnlyFinalizer
    {
        public (string IntermediatePath, string FinalPath)? Call { get; private set; }

        public void Finalize(string intermediatePath, string finalPath)
        {
            Call = (intermediatePath, finalPath);
            finalize(intermediatePath, finalPath);
        }
    }
}
