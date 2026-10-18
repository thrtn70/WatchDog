using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;

namespace WatchDog.Core.Tests.Recording;

public sealed class SessionSegmentReceiptTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "receipt-ledger-fixture");

    [Fact]
    public void ReceiptPreservesProducerIdentityAndOffsetsAndRedactsItsDiagnosticString()
    {
        var receipt = Receipt();
        var ledger = new SessionSegmentReceiptLedger(receipt.Context, Root);
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(receipt, out var media, out var failure));
        Assert.Null(failure);
        Assert.Equal(receipt.MediaId, media!.MediaId);
        Assert.Equal(receipt.Context.SessionId, media.SessionId);
        Assert.Equal(receipt.Context.Request.RequestId, media.CaptureRequestId);
        Assert.Equal(receipt.StartOffset, media.StartOffset);
        Assert.Equal(receipt.EndOffset - receipt.StartOffset, media.Duration);
        Assert.Equal(SessionTimingProvenance.RecorderStopwatchEstimate, media.TimingProvenance);
        Assert.Equal(new SessionSegmentProducerMetadata(1, receipt.OutputEpoch, receipt.ProducerTimingEpoch), media.ProducerMetadata);
        Assert.Equal(nameof(SessionSegmentReceipt), receipt.ToString());
        Assert.Equal(SessionSegmentReceiptDisposition.Duplicate, ledger.Accept(receipt, out var duplicate, out failure));
        Assert.Same(media, duplicate);
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("media")]
    [InlineData("output")]
    [InlineData("timing")]
    [InlineData("sequence")]
    [InlineData("negative")]
    [InlineData("reversed")]
    [InlineData("relative")]
    [InlineData("outside")]
    [InlineData("noncanonical")]
    [InlineData("native")]
    public void InvalidReceiptNeverConsumesAnIdentity(string change)
    {
        var receipt = Receipt();
        var invalid = change switch
        {
            "schema" => receipt with { SchemaVersion = 2 },
            "media" => receipt with { MediaId = Guid.Empty },
            "output" => receipt with { OutputEpoch = Guid.Empty },
            "timing" => receipt with { ProducerTimingEpoch = Guid.Empty },
            "sequence" => receipt with { Sequence = -1 },
            "negative" => receipt with { StartOffset = TimeSpan.FromTicks(-1) },
            "reversed" => receipt with { EndOffset = TimeSpan.Zero },
            "relative" => receipt with { FilePath = "relative.mkv" },
            "outside" => receipt with { FilePath = Path.Combine(Root + "-other", "segment.mkv") },
            "noncanonical" => receipt with { FilePath = Path.Combine(Root, "child", "..", "segment.mkv") },
            _ => receipt with { TimingProvenance = SessionTimingProvenance.NativeTimestamp },
        };
        var ledger = new SessionSegmentReceiptLedger(receipt.Context, Root);
        Assert.Equal(SessionSegmentReceiptDisposition.Rejected, ledger.Accept(invalid, out var media, out var failure));
        Assert.Null(media);
        Assert.NotNull(failure);
        Assert.DoesNotContain(invalid.FilePath, failure.Message);
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(receipt, out _, out _));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("request")]
    [InlineData("expectation")]
    [InlineData("requested")]
    [InlineData("actual")]
    public void FullProducerContextRejectsDrift(string change)
    {
        var receipt = Receipt();
        var context = receipt.Context;
        var request = context.Request;
        var altered = change switch
        {
            "session" => context with { SessionId = Guid.NewGuid() },
            "generation" => context with { Generation = context.Generation + 1 },
            "request" => context with { Request = new(Guid.NewGuid(), request.RequestedAt, request.Initiation, request.Target, request.Webcam, request.StartPolicy, request.StartupExpectation) },
            "expectation" => context with { Request = new(request.RequestId, request.RequestedAt, request.Initiation, request.Target, request.Webcam, request.StartPolicy) },
            "requested" => context with { RequestedTarget = new DisplayCaptureTarget("other") },
            _ => context with { ActualTarget = new DisplayCaptureTarget("other") },
        };
        var ledger = new SessionSegmentReceiptLedger(context, Root);
        Assert.Equal(SessionSegmentReceiptDisposition.Rejected, ledger.Accept(receipt with { Context = altered }, out _, out var failure));
        Assert.Equal("capture.segment-receipt.foreign", failure!.Code);
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(receipt, out _, out _));
    }

    [Fact]
    public void AdmissionManifestEnrichmentDoesNotChangeProducerCorrelation()
    {
        var receipt = Receipt();
        var enriched = receipt.Context with
        {
            AdmittedDevices = new CaptureDeviceManifest([AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "fixture-mic")]),
        };
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted,
            new SessionSegmentReceiptLedger(enriched, Root).Accept(receipt, out _, out _));
    }

    [Theory]
    [InlineData("media")]
    [InlineData("output")]
    [InlineData("path")]
    [InlineData("sequence")]
    [InlineData("overlap")]
    public void ConflictsCannotOverwriteAcceptedReceipt(string conflict)
    {
        var first = Receipt();
        var next = first with
        {
            MediaId = Guid.NewGuid(),
            OutputEpoch = Guid.NewGuid(),
            Sequence = 1,
            FilePath = Path.Combine(Root, "next.mkv"),
            StartOffset = first.EndOffset,
            EndOffset = first.EndOffset + TimeSpan.FromSeconds(1)
        };
        next = conflict switch
        {
            "media" => next with { MediaId = first.MediaId },
            "output" => next with { OutputEpoch = first.OutputEpoch },
            "path" => next with { FilePath = first.FilePath },
            "sequence" => next with { Sequence = first.Sequence },
            _ => next with { StartOffset = first.StartOffset },
        };
        var ledger = new SessionSegmentReceiptLedger(first.Context, Root);
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(first, out var original, out _));
        Assert.Equal(SessionSegmentReceiptDisposition.Rejected, ledger.Accept(next, out _, out var failure));
        Assert.Equal("capture.segment-receipt.conflict", failure!.Code);
        Assert.Equal(SessionSegmentReceiptDisposition.Duplicate, ledger.Accept(first, out var unchanged, out _));
        Assert.Same(original, unchanged);
    }

    [Fact]
    public void OutOfOrderCallbacksGapsAndIndependentStopwatchRestartsAreSupported()
    {
        var first = Receipt();
        var later = first with
        {
            MediaId = Guid.NewGuid(),
            OutputEpoch = Guid.NewGuid(),
            Sequence = 2,
            FilePath = Path.Combine(Root, "later.mkv"),
            StartOffset = TimeSpan.FromSeconds(20),
            EndOffset = TimeSpan.FromSeconds(30)
        };
        var restarted = first with
        {
            MediaId = Guid.NewGuid(),
            OutputEpoch = Guid.NewGuid(),
            ProducerTimingEpoch = Guid.NewGuid(),
            FilePath = Path.Combine(Root, "restart.mkv"),
            StartOffset = TimeSpan.Zero
        };
        var ledger = new SessionSegmentReceiptLedger(first.Context, Root);
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(later, out _, out _));
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(first, out _, out _));
        Assert.Equal(SessionSegmentReceiptDisposition.Accepted, ledger.Accept(restarted, out _, out _));
    }

    private static SessionSegmentReceipt Receipt()
    {
        var plan = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            new WindowCaptureTarget(12, new IntPtr(123), "Private title", "fixture.exe", "Fixture"),
            CaptureStartupBaselineResult.Create(new AppSettings())).Plan!;
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, plan.Request.Target);
        return new(1, context, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
            Path.Combine(Root, "segment.mkv"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
            SessionTimingProvenance.RecorderStopwatchEstimate);
    }
}
