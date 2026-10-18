using CoreBounds = WatchDog.Core.Capture.PhysicalCaptureBounds;
using MetadataBounds = WatchDog.CaptureMetadata.PhysicalCaptureBounds;
using MetadataDeviceRole = WatchDog.CaptureMetadata.CaptureDeviceRole;
using MetadataSelectionMode = WatchDog.CaptureMetadata.CaptureDeviceSelectionMode;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Metadata;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Metadata;

public sealed class CaptureMetadataMapperTests
{
    private static readonly Guid SessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RequestId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Fact]
    public void MapContextPreservesWindowCorrelationAndIndependentTargetsIncludingHighBitHandle()
    {
        var requested = new WindowCaptureTarget(
            42,
            new IntPtr(-1),
            WindowTitle: "Requested title",
            ExecutableName: "game.exe",
            WindowClass: "GameWindow");
        var requestTarget = requested with { WindowTitle = "Changed presentation title" };
        var actual = new WindowCaptureTarget(
            43,
            new IntPtr(0x1234),
            WindowTitle: "Actual title",
            ExecutableName: "capture-host.exe",
            WindowClass: "CaptureWindow");
        var context = CreateContext(requestTarget, requested, actual);

        var result = CaptureMetadataMapper.MapContext(
            context,
            CaptureMetadataMappingEvidence.Empty);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        var snapshot = Assert.IsType<WatchDog.CaptureMetadata.CaptureMetadataCorrelationSnapshot>(result.Value);
        Assert.Equal(SessionId, snapshot.SessionId);
        Assert.Equal(RequestId, snapshot.RequestId);
        Assert.Equal(7, snapshot.Generation);
        var mappedRequested = Assert.IsType<WatchDog.CaptureMetadata.ResolvedWindowTarget>(snapshot.RequestedTarget);
        Assert.Equal(42, mappedRequested.ProcessId);
        Assert.Equal(-1, mappedRequested.OpaqueWindowHandleValue);
        var mappedActual = Assert.IsType<WatchDog.CaptureMetadata.ResolvedWindowTarget>(snapshot.ActualTarget);
        Assert.Equal(43, mappedActual.ProcessId);
        Assert.Equal(0x1234, mappedActual.OpaqueWindowHandleValue);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void MapContextRejectsInvalidWindowIdentity(int processId, long handle)
    {
        var target = new WindowCaptureTarget(processId, new IntPtr(handle));
        var context = CreateContext(target, target, target);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        AssertFailure(result, MetadataMappingFailureReason.InvalidWindowIdentity);
    }

    [Fact]
    public void MapContextRejectsInvalidCorrelationAndUndefinedInitiation()
    {
        var target = new WindowCaptureTarget(10, new IntPtr(20));

        var emptySession = CreateContext(target, target, target) with { SessionId = Guid.Empty };
        AssertFailure(
            CaptureMetadataMapper.MapContext(emptySession, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.InvalidSessionId);

        var emptyRequest = new CaptureRequest(
            Guid.Empty,
            DateTimeOffset.UtcNow,
            CaptureInitiation.Manual,
            target);
        var emptyRequestContext = new CaptureSessionContext(SessionId, 7, emptyRequest, target, target);
        AssertFailure(
            CaptureMetadataMapper.MapContext(emptyRequestContext, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.InvalidRequestId);

        var invalidGeneration = CreateContext(target, target, target) with { Generation = 0 };
        AssertFailure(
            CaptureMetadataMapper.MapContext(invalidGeneration, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.InvalidGeneration);

        var undefinedRequest = new CaptureRequest(
            RequestId,
            DateTimeOffset.UtcNow,
            (CaptureInitiation)999,
            target);
        var undefinedContext = new CaptureSessionContext(SessionId, 7, undefinedRequest, target, target);
        AssertFailure(
            CaptureMetadataMapper.MapContext(undefinedContext, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.InvalidInitiation);
    }

    [Fact]
    public void MapContextRejectsGameTargetsWithoutSubstitutingActualWindow()
    {
        var game = new GameCaptureTarget("game-id", 42, "game.exe");
        var actual = new WindowCaptureTarget(42, new IntPtr(100));
        var context = CreateContext(game, game, actual);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        AssertFailure(result, MetadataMappingFailureReason.UnsupportedGameTarget);
    }

    [Fact]
    public void MapContextRequiresOrdinallyMatchingDisplayEvidenceAndPreservesNegativeOrigin()
    {
        var requestedTarget = new DisplayCaptureTarget("Display-A", "Requested label", 2);
        var actualTarget = new DisplayCaptureTarget("Display-B", "Actual label", 3);
        var context = CreateContext(
            requestedTarget with { DisplayName = "Changed presentation label" },
            requestedTarget,
            actualTarget);

        AssertFailure(
            CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.MissingDisplayEvidence);

        var mismatch = new CaptureMetadataMappingEvidence(
            new FrozenDisplayMappingEvidence("display-a", new CoreBounds(-1920, -20, 1920, 1080)),
            new FrozenDisplayMappingEvidence("Display-B", new CoreBounds(0, 40, 2560, 1440)));
        AssertFailure(
            CaptureMetadataMapper.MapContext(context, mismatch),
            MetadataMappingFailureReason.DisplayEvidenceIdentityMismatch);

        var sameIdentityContext = CreateContext(
            requestedTarget,
            requestedTarget,
            requestedTarget);
        var actualMismatch = new CaptureMetadataMappingEvidence(
            new FrozenDisplayMappingEvidence("Display-A", new CoreBounds(-1920, -20, 1920, 1080)),
            new FrozenDisplayMappingEvidence("display-a", new CoreBounds(-1920, -20, 1920, 1080)));
        AssertFailure(
            CaptureMetadataMapper.MapContext(sameIdentityContext, actualMismatch),
            MetadataMappingFailureReason.DisplayEvidenceIdentityMismatch);

        var evidence = new CaptureMetadataMappingEvidence(
            new FrozenDisplayMappingEvidence("Display-A", new CoreBounds(-1920, -20, 1920, 1080)),
            new FrozenDisplayMappingEvidence("Display-B", new CoreBounds(0, 40, 2560, 1440)));
        var success = CaptureMetadataMapper.MapContext(context, evidence);

        Assert.True(success.Succeeded);
        var snapshot = Assert.IsType<WatchDog.CaptureMetadata.CaptureMetadataCorrelationSnapshot>(success.Value);
        var requested = Assert.IsType<WatchDog.CaptureMetadata.ResolvedDisplayTarget>(snapshot.RequestedTarget);
        Assert.Equal("Display-A", requested.StableDisplayId);
        Assert.Equal(new MetadataBounds(-1920, -20, 1920, 1080), requested.FrozenPhysicalBounds);
        var actual = Assert.IsType<WatchDog.CaptureMetadata.ResolvedDisplayTarget>(snapshot.ActualTarget);
        Assert.Equal("Display-B", actual.StableDisplayId);
        Assert.Equal(new MetadataBounds(0, 40, 2560, 1440), actual.FrozenPhysicalBounds);
    }

    [Fact]
    public void MapContextRejectsNullInputsAndUnexpectedDisplayEvidence()
    {
        var target = new WindowCaptureTarget(42, new IntPtr(100));
        var context = CreateContext(target, target, target);

        AssertFailure(
            CaptureMetadataMapper.MapContext(null, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.NullSource);
        AssertFailure(
            CaptureMetadataMapper.MapContext(context, null),
            MetadataMappingFailureReason.MissingMappingEvidence);

        var unexpected = new CaptureMetadataMappingEvidence(
            new FrozenDisplayMappingEvidence("Display-A", new CoreBounds(0, 0, 1920, 1080)),
            null);
        AssertFailure(
            CaptureMetadataMapper.MapContext(context, unexpected),
            MetadataMappingFailureReason.UnexpectedDisplayEvidence);
    }

    [Fact]
    public void MapContextRejectsRequestTargetPhysicalMismatch()
    {
        var requested = new WindowCaptureTarget(42, new IntPtr(100), "Title", "game.exe", "Class-A");
        var requestTarget = requested with { WindowClass = "Class-B" };
        var context = CreateContext(requestTarget, requested, requested);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        AssertFailure(result, MetadataMappingFailureReason.RequestTargetMismatch);
    }

    [Fact]
    public void MapContextPreservesValidRegionAndRejectsOverflowingEndpoint()
    {
        var target = new RegionCaptureTarget(
            "DISPLAY-A",
            new CoreBounds(-1900, -1000, 100, 200),
            new CoreBounds(-1920, -1080, 1920, 1080));
        var context = CreateContext(target, target, target);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<WatchDog.CaptureMetadata.CaptureMetadataCorrelationSnapshot>(result.Value);
        var region = Assert.IsType<WatchDog.CaptureMetadata.ResolvedRegionTarget>(snapshot.RequestedTarget);
        Assert.Equal("DISPLAY-A", region.OwningDisplayId);
        Assert.Equal(new MetadataBounds(-1900, -1000, 100, 200), region.SelectedPhysicalBounds);
        Assert.Equal(new MetadataBounds(-1920, -1080, 1920, 1080), region.FrozenOwningDisplayBounds);

        var overflow = new RegionCaptureTarget(
            "DISPLAY-A",
            new CoreBounds(int.MaxValue - 5, 0, 10, 10),
            new CoreBounds(int.MaxValue - 5, 0, 10, 10));
        var overflowContext = CreateContext(overflow, overflow, overflow);
        AssertFailure(
            CaptureMetadataMapper.MapContext(overflowContext, CaptureMetadataMappingEvidence.Empty),
            MetadataMappingFailureReason.InvalidRegionGeometry);
    }

    [Fact]
    public void MapContextMapsAdmittedExactDefaultAndWebcamSelectionsOnly()
    {
        var target = new WindowCaptureTarget(42, new IntPtr(100));
        var webcam = CreateWebcam("Camera-A");
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "Output-A"),
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.Microphone),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "Camera-A"),
        ]);
        var context = CreateContext(target, target, target, webcam, manifest);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        Assert.True(result.Succeeded);
        var snapshot = Assert.IsType<WatchDog.CaptureMetadata.CaptureMetadataCorrelationSnapshot>(result.Value);
        Assert.Collection(
            snapshot.DeviceSelections,
            output =>
            {
                Assert.Equal(MetadataDeviceRole.DesktopAudio, output.Role);
                Assert.Equal(MetadataSelectionMode.Exact, output.Mode);
                Assert.Equal("Output-A", output.OpaqueDeviceId);
            },
            microphone =>
            {
                Assert.Equal(MetadataDeviceRole.Microphone, microphone.Role);
                Assert.Equal(MetadataSelectionMode.SystemDefault, microphone.Mode);
                Assert.Null(microphone.OpaqueDeviceId);
            },
            camera =>
            {
                Assert.Equal(MetadataDeviceRole.Webcam, camera.Role);
                Assert.Equal(MetadataSelectionMode.Exact, camera.Mode);
                Assert.Equal("Camera-A", camera.OpaqueDeviceId);
            });
    }

    [Fact]
    public void MapContextDoesNotManufactureRequestOnlyWebcamAdmission()
    {
        var target = new WindowCaptureTarget(42, new IntPtr(100));
        var context = CreateContext(target, target, target, CreateWebcam("Camera-A"));

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.DeviceSelections);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Camera-B")]
    [InlineData("camera-a")]
    public void MapContextRejectsUncorrelatedWebcamAdmission(string? requestedWebcamId)
    {
        var target = new WindowCaptureTarget(42, new IntPtr(100));
        var manifest = new CaptureDeviceManifest(
            [AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "Camera-A")]);
        var context = CreateContext(
            target,
            target,
            target,
            requestedWebcamId is null ? null : CreateWebcam(requestedWebcamId),
            manifest);

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        AssertFailure(result, MetadataMappingFailureReason.WebcamAdmissionMismatch);
    }

    [Fact]
    public void MapContextRejectsWebcamAdmissionOutsideManualInitiation()
    {
        var target = new WindowCaptureTarget(42, new IntPtr(100));
        var request = new CaptureRequest(
            RequestId,
            DateTimeOffset.UtcNow,
            CaptureInitiation.Automatic,
            target);
        var context = new CaptureSessionContext(SessionId, 7, request, target, target)
        {
            AdmittedDevices = new CaptureDeviceManifest(
                [AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "Camera-A")]),
        };

        var result = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        AssertFailure(result, MetadataMappingFailureReason.WebcamAdmissionMismatch);
    }

    [Theory]
    [InlineData(AudioEndpointDirection.Output, MetadataDeviceRole.DesktopAudio)]
    [InlineData(AudioEndpointDirection.Input, MetadataDeviceRole.Microphone)]
    public void MapAudioCatalogMapsDirectionAndOrdinalDefault(
        AudioEndpointDirection direction,
        MetadataDeviceRole expectedRole)
    {
        var source = AudioDeviceCatalogSnapshot.CreateValidated(
            direction,
            WindowsAudioEndpointRole.Multimedia,
            [
                new AudioDeviceInfo("Device-A", "First", IsDefault: false),
                new AudioDeviceInfo("device-a", "Second", IsDefault: false),
            ],
            "Device-A");

        var result = CaptureMetadataMapper.MapAudioCatalog(source);

        Assert.True(result.Succeeded);
        Assert.Collection(
            result.Value!.Entries,
            first =>
            {
                Assert.Equal(expectedRole, first.Role);
                Assert.Equal("Device-A", first.OpaqueDeviceId);
                Assert.True(first.IsSystemDefault);
            },
            second =>
            {
                Assert.Equal(expectedRole, second.Role);
                Assert.Equal("device-a", second.OpaqueDeviceId);
                Assert.False(second.IsSystemDefault);
            });
    }

    [Fact]
    public void MapAudioCatalogPreservesEmptyAndNoDefaultCatalogs()
    {
        var empty = AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            WindowsAudioEndpointRole.Multimedia,
            [],
            null);
        var noDefault = AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Input,
            WindowsAudioEndpointRole.Multimedia,
            [new AudioDeviceInfo("Mic-A", "Microphone", IsDefault: false)],
            null);

        var emptyResult = CaptureMetadataMapper.MapAudioCatalog(empty);
        var noDefaultResult = CaptureMetadataMapper.MapAudioCatalog(noDefault);

        Assert.True(emptyResult.Succeeded);
        Assert.Empty(emptyResult.Value!.Entries);
        Assert.True(noDefaultResult.Succeeded);
        Assert.False(Assert.Single(noDefaultResult.Value!.Entries).IsSystemDefault);
    }

    [Theory]
    [InlineData(WindowsAudioEndpointRole.Console)]
    [InlineData(WindowsAudioEndpointRole.Communications)]
    public void MapAudioCatalogRejectsNonMultimediaDefaultRole(WindowsAudioEndpointRole role)
    {
        var source = AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            role,
            [],
            null);

        var result = CaptureMetadataMapper.MapAudioCatalog(source);

        AssertFailure(result, MetadataMappingFailureReason.UnsupportedAudioDefaultRole);
    }

    [Fact]
    public void MapWebcamCatalogValidatesAndSnapshotsAlreadyAcquiredMetadata()
    {
        var devices = new List<WebcamDeviceInfo>
        {
            new("Camera-A", "First camera"),
            new("camera-a", "Second camera"),
        };

        var result = CaptureMetadataMapper.MapWebcamCatalog(devices);
        devices.Clear();

        Assert.True(result.Succeeded);
        Assert.Collection(
            result.Value!.Entries,
            first =>
            {
                Assert.Equal(MetadataDeviceRole.Webcam, first.Role);
                Assert.Equal("Camera-A", first.OpaqueDeviceId);
                Assert.False(first.IsSystemDefault);
            },
            second =>
            {
                Assert.Equal("camera-a", second.OpaqueDeviceId);
                Assert.False(second.IsSystemDefault);
            });
    }

    [Fact]
    public void MapWebcamCatalogRejectsMalformedAndDuplicateMetadata()
    {
        AssertFailure(
            CaptureMetadataMapper.MapWebcamCatalog(
                [new WebcamDeviceInfo("Camera-A", " ")]),
            MetadataMappingFailureReason.InvalidWebcamCatalog);
        AssertFailure(
            CaptureMetadataMapper.MapWebcamCatalog(
            [
                new WebcamDeviceInfo("Camera-A", "First"),
                new WebcamDeviceInfo("Camera-A", "Duplicate"),
            ]),
            MetadataMappingFailureReason.InvalidWebcamCatalog);
    }

    [Fact]
    public void ResultAndEvidenceStringsDoNotExposePrivateIdentity()
    {
        var privateId = "secret-display-id";
        var target = new DisplayCaptureTarget(privateId);
        var context = CreateContext(target, target, target);
        var evidence = new CaptureMetadataMappingEvidence(
            new FrozenDisplayMappingEvidence(privateId, new CoreBounds(-1234, 5678, 10, 20)),
            new FrozenDisplayMappingEvidence(privateId, new CoreBounds(-1234, 5678, 10, 20)));

        var success = CaptureMetadataMapper.MapContext(context, evidence);
        var failure = CaptureMetadataMapper.MapContext(context, CaptureMetadataMappingEvidence.Empty);

        foreach (var text in new[] { success.ToString(), failure.ToString(), evidence.ToString() })
        {
            Assert.DoesNotContain(privateId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SessionId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RequestId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1234", text, StringComparison.Ordinal);
            Assert.DoesNotContain("5678", text, StringComparison.Ordinal);
        }
    }

    private static CaptureSessionContext CreateContext(
        CaptureTarget requestTarget,
        CaptureTarget requestedTarget,
        CaptureTarget actualTarget,
        WebcamCaptureSelection? webcam = null,
        CaptureDeviceManifest? manifest = null)
    {
        var request = new CaptureRequest(
            RequestId,
            DateTimeOffset.Parse("2026-09-20T00:00:00Z"),
            CaptureInitiation.Manual,
            requestTarget,
            webcam);
        return new CaptureSessionContext(SessionId, 7, request, requestedTarget, actualTarget)
        {
            AdmittedDevices = manifest ?? CaptureDeviceManifest.Empty,
        };
    }

    private static WebcamCaptureSelection CreateWebcam(string deviceId) =>
        new(deviceId, "Camera", 1920, 1080, 30, "MJPEG", 6000);

    private static void AssertFailure<T>(
        MetadataMappingResult<T> result,
        MetadataMappingFailureReason reason)
        where T : class
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Equal(reason, result.FailureReason);
    }
}
