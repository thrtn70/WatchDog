using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;

namespace WatchDog.Core.Tests.Recording;

public sealed class ObsSessionRecorderTests
{
    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData(" MKV ", "mkv")]
    public void SeparateTracksAllowOnlyNormalizedMp4OrMkv(string input, string expected)
    {
        Assert.Equal(expected, ObsSessionRecorder.NormalizeFileFormat(input, separateAudioTracks: true));
    }

    [Theory]
    [InlineData("flv")]
    [InlineData("mov")]
    [InlineData("")]
    public void SeparateTracksRejectUnsupportedOrMissingFormat(string input)
    {
        Assert.Throws<NotSupportedException>(() =>
            ObsSessionRecorder.NormalizeFileFormat(input, separateAudioTracks: true));
    }

    [Fact]
    public void EncodersRemainOwnedUntilOutputReleaseSucceeds()
    {
        var releases = 0;

        ObsSessionRecorder.ReleaseEncodersIfOutputReleased(
            outputReleased: false,
            () => releases++);
        Assert.Equal(0, releases);

        ObsSessionRecorder.ReleaseEncodersIfOutputReleased(
            outputReleased: true,
            () => releases++);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void FinalizedMedia_MustExistAndBeNonEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WD_recorder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var missing = Path.Combine(directory, "missing.mkv");
            var empty = Path.Combine(directory, "empty.mkv");
            var valid = Path.Combine(directory, "valid.mkv");
            File.WriteAllBytes(empty, []);
            File.WriteAllBytes(valid, [1]);

            Assert.Throws<IOException>(() => ObsSessionRecorder.ValidateFinalizedMedia(null));
            Assert.Throws<IOException>(() => ObsSessionRecorder.ValidateFinalizedMedia(missing));
            Assert.Throws<IOException>(() => ObsSessionRecorder.ValidateFinalizedMedia(empty));
            ObsSessionRecorder.ValidateFinalizedMedia(valid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScheduledCallback_CurrentRecordingGeneration_IsAccepted()
    {
        Assert.True(ObsSessionRecorder.ShouldProcessScheduledCallback(
            expectedGeneration: 12,
            currentGeneration: 12,
            isRecording: true,
            isDisposing: false));
    }

    [Fact]
    public void ScheduledCallback_StaleRecordingGeneration_IsRejected()
    {
        Assert.False(ObsSessionRecorder.ShouldProcessScheduledCallback(
            expectedGeneration: 11,
            currentGeneration: 12,
            isRecording: true,
            isDisposing: false));
    }

    [Fact]
    public void ScheduledCallback_WhenRecordingStopped_IsRejected()
    {
        Assert.False(ObsSessionRecorder.ShouldProcessScheduledCallback(
            expectedGeneration: 12,
            currentGeneration: 12,
            isRecording: false,
            isDisposing: false));
    }

    [Fact]
    public void ScheduledCallback_WhileDisposing_IsRejected()
    {
        Assert.False(ObsSessionRecorder.ShouldProcessScheduledCallback(
            expectedGeneration: 12,
            currentGeneration: 12,
            isRecording: true,
            isDisposing: true));
    }

    [Fact]
    public void ProducerReceiptState_RotationFreezesOriginalIdentityAndAdvancesWithoutMutation()
    {
        var context = CreateBoundContext();
        var initial = ObsSessionRecorder.BeginProducerReceiptState(context);
        var firstMediaId = Guid.NewGuid();
        var firstOutputEpoch = Guid.NewGuid();
        var firstPath = TestPath("first.mkv");
        var secondPath = TestPath("second.mkv");

        var first = Assert.IsType<SessionSegmentReceipt>(ObsSessionRecorder.FreezeProducerReceipt(
            initial,
            firstMediaId,
            firstOutputEpoch,
            firstPath,
            TimeSpan.FromSeconds(5)));
        var successor = ObsSessionRecorder.AdvanceProducerReceiptState(initial, first.EndOffset);
        var second = Assert.IsType<SessionSegmentReceipt>(ObsSessionRecorder.FreezeProducerReceipt(
            successor,
            Guid.NewGuid(),
            Guid.NewGuid(),
            secondPath,
            TimeSpan.FromSeconds(9)));

        Assert.Same(context, first.Context);
        Assert.NotNull(first.Context.Request.StartupExpectation);
        Assert.Equal(1, first.SchemaVersion);
        Assert.Equal(firstMediaId, first.MediaId);
        Assert.Equal(firstOutputEpoch, first.OutputEpoch);
        Assert.NotEqual(Guid.Empty, first.ProducerTimingEpoch);
        Assert.Equal(0, first.Sequence);
        Assert.Equal(TimeSpan.Zero, first.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(5), first.EndOffset);
        Assert.Equal(SessionTimingProvenance.RecorderStopwatchEstimate, first.TimingProvenance);

        Assert.Same(context, second.Context);
        Assert.Equal(first.ProducerTimingEpoch, second.ProducerTimingEpoch);
        Assert.NotEqual(first.MediaId, second.MediaId);
        Assert.NotEqual(first.OutputEpoch, second.OutputEpoch);
        Assert.Equal(1, second.Sequence);
        Assert.Equal(first.EndOffset, second.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(9), second.EndOffset);

        Assert.Equal(0, initial.Sequence);
        Assert.Equal(TimeSpan.Zero, initial.StartOffset);
        Assert.Equal(firstMediaId, first.MediaId);
        Assert.Equal(firstOutputEpoch, first.OutputEpoch);
        Assert.Equal(firstPath, first.FilePath);
    }

    [Fact]
    public void ProducerReceiptState_GenuineRestartMintsNewTimingEpochAndResetsSequence()
    {
        var context = CreateBoundContext();

        var firstRun = ObsSessionRecorder.BeginProducerReceiptState(context);
        var restarted = ObsSessionRecorder.BeginProducerReceiptState(context);

        Assert.NotEqual(Guid.Empty, firstRun.ProducerTimingEpoch);
        Assert.NotEqual(Guid.Empty, restarted.ProducerTimingEpoch);
        Assert.NotEqual(firstRun.ProducerTimingEpoch, restarted.ProducerTimingEpoch);
        Assert.Equal(0, firstRun.Sequence);
        Assert.Equal(0, restarted.Sequence);
        Assert.Equal(TimeSpan.Zero, firstRun.StartOffset);
        Assert.Equal(TimeSpan.Zero, restarted.StartOffset);
    }

    [Fact]
    public void ConfirmFinalizedSegment_SuccessRetainsFrozenReceipt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WD_receipt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "segment.mkv");
            File.WriteAllBytes(path, [1]);
            var receipt = CreateReceipt(path);

            var finalized = Assert.IsType<ObsSessionRecorder.FinalizedSegmentNotification>(
                ObsSessionRecorder.ConfirmFinalizedSegment(
                    finalized: true,
                    filePath: path,
                    receipt: receipt,
                    validateMedia: ObsSessionRecorder.ValidateFinalizedMedia));

            Assert.Equal(path, finalized.FilePath);
            Assert.Same(receipt, finalized.Receipt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfirmFinalizedSegment_StopFailureSuppressesReceiptAndLegacyPath()
    {
        var validationCalls = 0;
        var receiptCalls = 0;
        var legacyCalls = 0;
        var receipt = CreateReceipt(TestPath("failed.mkv"));

        var finalized = ObsSessionRecorder.ConfirmFinalizedSegment(
            finalized: false,
            filePath: receipt.FilePath,
            receipt: receipt,
            validateMedia: _ => validationCalls++);
        ObsSessionRecorder.PublishFinalizedSegment(
            finalized,
            _ => receiptCalls++,
            _ => legacyCalls++);

        Assert.Null(finalized);
        Assert.Equal(0, validationCalls);
        Assert.Equal(0, receiptCalls);
        Assert.Equal(0, legacyCalls);
    }

    [Fact]
    public void ConfirmFinalizedSegment_ValidationFailureProducesNoPublishablePacket()
    {
        var receiptCalls = 0;
        var legacyCalls = 0;
        var receipt = CreateReceipt(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mkv"));
        ObsSessionRecorder.FinalizedSegmentNotification? finalized = null;

        Assert.Throws<IOException>(() => finalized = ObsSessionRecorder.ConfirmFinalizedSegment(
            finalized: true,
            filePath: receipt.FilePath,
            receipt: receipt,
            validateMedia: ObsSessionRecorder.ValidateFinalizedMedia));
        ObsSessionRecorder.PublishFinalizedSegment(
            finalized,
            _ => receiptCalls++,
            _ => legacyCalls++);

        Assert.Null(finalized);
        Assert.Equal(0, receiptCalls);
        Assert.Equal(0, legacyCalls);
    }

    [Fact]
    public void PublishFinalizedSegment_BoundSuccessPublishesReceiptAndLegacyPathExactlyOnce()
    {
        var receipt = CreateReceipt(TestPath("bound.mkv"));
        var finalized = new ObsSessionRecorder.FinalizedSegmentNotification(receipt.FilePath, receipt);
        SessionSegmentReceipt? observedReceipt = null;
        string? observedPath = null;
        var receiptCalls = 0;
        var legacyCalls = 0;

        ObsSessionRecorder.PublishFinalizedSegment(
            finalized,
            value => { receiptCalls++; observedReceipt = value; },
            value => { legacyCalls++; observedPath = value; });

        Assert.Equal(1, receiptCalls);
        Assert.Equal(1, legacyCalls);
        Assert.Same(receipt, observedReceipt);
        Assert.Equal(receipt.FilePath, observedPath);
    }

    [Fact]
    public void PublishFinalizedSegment_ContextlessSuccessPreservesOnlyLegacyPathCallback()
    {
        var legacyPath = TestPath("legacy.mkv");
        var state = ObsSessionRecorder.BeginProducerReceiptState(context: null);
        var receipt = ObsSessionRecorder.FreezeProducerReceipt(
            state,
            Guid.NewGuid(),
            Guid.NewGuid(),
            legacyPath,
            TimeSpan.FromSeconds(4));
        var finalized = new ObsSessionRecorder.FinalizedSegmentNotification(legacyPath, receipt);
        var receiptCalls = 0;
        var legacyCalls = 0;

        ObsSessionRecorder.PublishFinalizedSegment(
            finalized,
            _ => receiptCalls++,
            _ => legacyCalls++);

        Assert.Null(receipt);
        Assert.Equal(0, receiptCalls);
        Assert.Equal(1, legacyCalls);
    }

    [Fact]
    public void PublishFinalizedSegment_ReceiptFailureStillPublishesLegacyPath()
    {
        var receipt = CreateReceipt(TestPath("subscriber-failure.mkv"));
        var finalized = new ObsSessionRecorder.FinalizedSegmentNotification(receipt.FilePath, receipt);
        var retained = new List<SessionSegmentReceipt>();
        var legacyCalls = 0;
        ObsSessionRecorder.RetainFinalizedSegment(retained, finalized);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ObsSessionRecorder.PublishFinalizedSegment(
                finalized,
                _ => throw new InvalidOperationException("receipt subscriber failed"),
                _ => legacyCalls++));

        Assert.Equal("receipt subscriber failed", failure.Message);
        Assert.Equal(1, legacyCalls);
        Assert.Same(
            receipt,
            Assert.Single(ObsSessionRecorder.SnapshotFinalizedSegments(retained, receipt.Context)));
    }

    [Fact]
    public void FinalizedReceiptSnapshot_PrecedesDelayedPublicationAndMatchesEnrichedContext()
    {
        var receipt = CreateReceipt(TestPath("delayed.mkv"));
        var finalized = new ObsSessionRecorder.FinalizedSegmentNotification(receipt.FilePath, receipt);
        var retained = new List<SessionSegmentReceipt>();
        var enriched = receipt.Context with
        {
            AdmittedDevices = new CaptureDeviceManifest(
            [
                AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "admitted-mic"),
            ]),
        };
        var receiptCallbacks = 0;
        var legacyCallbacks = 0;

        ObsSessionRecorder.RetainFinalizedSegment(retained, finalized);
        var beforePublication = ObsSessionRecorder.SnapshotFinalizedSegments(retained, enriched);

        Assert.Single(beforePublication);
        Assert.Same(receipt, beforePublication[0]);
        Assert.Equal(0, receiptCallbacks);
        Assert.Equal(0, legacyCallbacks);
        Assert.Empty(ObsSessionRecorder.SnapshotFinalizedSegments(
            retained,
            enriched with { SessionId = Guid.NewGuid() }));

        retained.Clear();
        Assert.Single(beforePublication);
        ObsSessionRecorder.PublishFinalizedSegment(
            finalized,
            _ => receiptCallbacks++,
            _ => legacyCallbacks++);

        Assert.Equal(1, receiptCallbacks);
        Assert.Equal(1, legacyCallbacks);
    }

    [Fact]
    public async Task SynchronousDisposalSeam_WaitsForOperationGate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var attempting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanup = Task.Run(() =>
        {
            attempting.SetResult(true);
            ObsSessionRecorder.RunUnderOperationGate(
                gate,
                () => cleanupRan.SetResult(true));
        });

        await attempting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cleanupRan.Task.IsCompleted);

        gate.Release();

        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanupRan.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ScheduledCallbackDrain_WaitsForAcceptedCallbackCompletion()
    {
        using var recorder = CreateRecorder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.QueueScheduledCallback(async () =>
        {
            entered.SetResult();
            await release.Task;
            completed.SetResult();
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = Task.Run(recorder.WaitForScheduledCallbacks);
        Assert.False(drain.IsCompleted);

        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ScheduledCallbackAdmission_ClosesAfterDispose()
    {
        var recorder = CreateRecorder();
        recorder.Dispose();
        var called = false;

        recorder.QueueScheduledCallback(() =>
        {
            called = true;
            return Task.CompletedTask;
        });
        recorder.WaitForScheduledCallbacks();

        Assert.False(called);
    }

    [Fact]
    public async Task ScheduledCallback_CanDisposeReentrantlyWithoutSelfDeadlock()
    {
        var recorder = CreateRecorder();
        var disposedInsideCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = Task.Run(() => recorder.QueueScheduledCallback(() =>
        {
            recorder.Dispose();
            disposedInsideCallback.SetResult();
            return Task.CompletedTask;
        }));

        await disposedInsideCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        recorder.WaitForScheduledCallbacks();

        var admittedAfterDispose = false;
        recorder.QueueScheduledCallback(() =>
        {
            admittedAfterDispose = true;
            return Task.CompletedTask;
        });
        recorder.WaitForScheduledCallbacks();
        Assert.False(admittedAfterDispose);
    }

    private static ObsSessionRecorder CreateRecorder() => new(
        new SessionRecordingConfig(),
        videoBitrate: 1_500,
        audioBitrate: 96,
        audioTrackPlan: AudioTrackPlan.Create(separateAudioTracks: false),
        logger: NullLogger<ObsSessionRecorder>.Instance);

    private static CaptureSessionContext CreateBoundContext()
    {
        var baseline = CaptureStartupBaselineResult.Create(
            new AppSettings(),
            Guid.Parse("cb770ff3-cee4-4c5e-a098-b7eb7e3d7570"));
        var target = new WindowCaptureTarget(
            42,
            new IntPtr(0x4200),
            "Tutorial",
            "tutorial.exe",
            "TutorialClass");
        var plan = TutorialRecordingPolicy.Create(
            Guid.Parse("e595d234-28ba-4e9f-9488-d8068df68395"),
            DateTimeOffset.UnixEpoch,
            target,
            baseline).Plan!;
        return new CaptureSessionContext(
            Guid.Parse("e04566c1-f8c1-46b2-a5ba-913b65a6177d"),
            7,
            plan.Request,
            target,
            target);
    }

    private static SessionSegmentReceipt CreateReceipt(string filePath) => new(
        SchemaVersion: 1,
        Context: CreateBoundContext(),
        MediaId: Guid.Parse("88335625-1a33-4333-89f1-93f5206fe652"),
        OutputEpoch: Guid.Parse("7582b833-7401-4fa5-9192-e4ff5a1f1a42"),
        ProducerTimingEpoch: Guid.Parse("157a745a-aae0-45cc-ad89-508f515ed829"),
        Sequence: 0,
        FilePath: filePath,
        StartOffset: TimeSpan.Zero,
        EndOffset: TimeSpan.FromSeconds(4),
        TimingProvenance: SessionTimingProvenance.RecorderStopwatchEstimate);

    private static string TestPath(string fileName) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WD_receipt_tests", fileName));
}
