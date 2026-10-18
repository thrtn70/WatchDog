using CoreBounds = WatchDog.Core.Capture.PhysicalCaptureBounds;
using CoreDeviceRole = WatchDog.Core.Devices.CaptureDeviceRole;
using CoreSelectionMode = WatchDog.Core.Devices.CaptureDeviceSelectionMode;
using MetadataBounds = WatchDog.CaptureMetadata.PhysicalCaptureBounds;
using MetadataDeviceRole = WatchDog.CaptureMetadata.CaptureDeviceRole;
using WatchDog.CaptureMetadata;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Metadata;

internal static class CaptureMetadataMapper
{
    internal static MetadataMappingResult<CaptureMetadataCorrelationSnapshot> MapContext(
        CaptureSessionContext? source,
        CaptureMetadataMappingEvidence? evidence)
    {
        if (source is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.NullSource);
        if (evidence is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingMappingEvidence);
        if (source.SessionId == Guid.Empty)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.InvalidSessionId);
        if (source.Generation <= 0)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.InvalidGeneration);
        if (source.Request is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingRequest);
        if (source.Request.RequestId == Guid.Empty)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.InvalidRequestId);
        if (!Enum.IsDefined(source.Request.Initiation))
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.InvalidInitiation);
        if (source.Request.Target is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingRequestTarget);
        if (source.RequestedTarget is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingRequestedTarget);
        if (source.ActualTarget is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingActualTarget);
        if (source.AdmittedDevices is null)
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.MissingDeviceManifest);
        if (!HasSamePhysicalIdentity(source.Request.Target, source.RequestedTarget))
            return Failure<CaptureMetadataCorrelationSnapshot>(MetadataMappingFailureReason.RequestTargetMismatch);

        var webcamFailure = ValidateWebcamAdmission(source.Request, source.AdmittedDevices);
        if (webcamFailure is not null)
            return Failure<CaptureMetadataCorrelationSnapshot>(webcamFailure.Value);

        try
        {
            if (!TryMapTarget(
                    source.RequestedTarget,
                    evidence.RequestedDisplay,
                    out var requestedTarget,
                    out var requestedFailure))
            {
                return Failure<CaptureMetadataCorrelationSnapshot>(requestedFailure);
            }

            if (!TryMapTarget(
                    source.ActualTarget,
                    evidence.ActualDisplay,
                    out var actualTarget,
                    out var actualFailure))
            {
                return Failure<CaptureMetadataCorrelationSnapshot>(actualFailure);
            }

            var selections = new List<CaptureDeviceSelection>(source.AdmittedDevices.Entries.Count);
            foreach (var admittedDevice in source.AdmittedDevices.Entries)
            {
                if (!TryMapDevice(admittedDevice, out var selection, out var deviceFailure))
                    return Failure<CaptureMetadataCorrelationSnapshot>(deviceFailure);

                selections.Add(selection!);
            }

            var snapshot = new CaptureMetadataCorrelationSnapshot(
                source.SessionId,
                source.Request.RequestId,
                source.Generation,
                requestedTarget!,
                actualTarget!,
                selections);
            return MetadataMappingResult<CaptureMetadataCorrelationSnapshot>.Success(snapshot);
        }
        catch (ArgumentException)
        {
            return Failure<CaptureMetadataCorrelationSnapshot>(
                MetadataMappingFailureReason.DestinationContractRejected);
        }
    }

    internal static MetadataMappingResult<CaptureDeviceCatalogSnapshot> MapAudioCatalog(
        AudioDeviceCatalogSnapshot? source)
    {
        if (source is null)
            return Failure<CaptureDeviceCatalogSnapshot>(MetadataMappingFailureReason.NullSource);
        if (source.WindowsDefaultRole != WindowsAudioEndpointRole.Multimedia)
        {
            return Failure<CaptureDeviceCatalogSnapshot>(
                MetadataMappingFailureReason.UnsupportedAudioDefaultRole);
        }

        var role = source.Direction switch
        {
            AudioEndpointDirection.Output => MetadataDeviceRole.DesktopAudio,
            AudioEndpointDirection.Input => MetadataDeviceRole.Microphone,
            _ => (MetadataDeviceRole?)null,
        };
        if (role is null)
            return Failure<CaptureDeviceCatalogSnapshot>(MetadataMappingFailureReason.UnsupportedAudioDirection);

        try
        {
            var entries = source.ActiveDevices
                .Select(device => new CaptureDeviceCatalogEntry(
                    role.Value,
                    device.DeviceId,
                    source.WindowsDefaultEndpointId is not null &&
                    StringComparer.Ordinal.Equals(device.DeviceId, source.WindowsDefaultEndpointId)))
                .ToArray();
            return MetadataMappingResult<CaptureDeviceCatalogSnapshot>.Success(
                new CaptureDeviceCatalogSnapshot(entries));
        }
        catch (ArgumentException)
        {
            return Failure<CaptureDeviceCatalogSnapshot>(
                MetadataMappingFailureReason.DestinationContractRejected);
        }
    }

    internal static MetadataMappingResult<CaptureDeviceCatalogSnapshot> MapWebcamCatalog(
        IEnumerable<WebcamDeviceInfo>? devices)
    {
        if (devices is null)
            return Failure<CaptureDeviceCatalogSnapshot>(MetadataMappingFailureReason.NullSource);

        var frozen = devices.ToArray();
        if (frozen.Any(device =>
                device is null ||
                string.IsNullOrWhiteSpace(device.DeviceId) ||
                string.IsNullOrWhiteSpace(device.DisplayName)))
        {
            return Failure<CaptureDeviceCatalogSnapshot>(
                MetadataMappingFailureReason.InvalidWebcamCatalog);
        }

        var duplicate = frozen
            .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1);
        if (duplicate)
        {
            return Failure<CaptureDeviceCatalogSnapshot>(
                MetadataMappingFailureReason.InvalidWebcamCatalog);
        }

        try
        {
            var entries = frozen.Select(device => new CaptureDeviceCatalogEntry(
                MetadataDeviceRole.Webcam,
                device.DeviceId,
                isSystemDefault: false));
            return MetadataMappingResult<CaptureDeviceCatalogSnapshot>.Success(
                new CaptureDeviceCatalogSnapshot(entries));
        }
        catch (ArgumentException)
        {
            return Failure<CaptureDeviceCatalogSnapshot>(
                MetadataMappingFailureReason.DestinationContractRejected);
        }
    }

    private static bool TryMapTarget(
        CaptureTarget source,
        FrozenDisplayMappingEvidence? displayEvidence,
        out ResolvedCaptureTarget? target,
        out MetadataMappingFailureReason failure)
    {
        target = null;
        failure = default;

        switch (source)
        {
            case GameCaptureTarget:
                failure = MetadataMappingFailureReason.UnsupportedGameTarget;
                return false;

            case WindowCaptureTarget window:
                if (displayEvidence is not null)
                {
                    failure = MetadataMappingFailureReason.UnexpectedDisplayEvidence;
                    return false;
                }
                if (window.ProcessId <= 0 || window.WindowHandle == IntPtr.Zero)
                {
                    failure = MetadataMappingFailureReason.InvalidWindowIdentity;
                    return false;
                }

                target = new ResolvedWindowTarget(window.ProcessId, window.WindowHandle.ToInt64());
                return true;

            case DisplayCaptureTarget display:
                if (string.IsNullOrWhiteSpace(display.StableDisplayId))
                {
                    failure = MetadataMappingFailureReason.InvalidDisplayIdentity;
                    return false;
                }
                if (displayEvidence is null)
                {
                    failure = MetadataMappingFailureReason.MissingDisplayEvidence;
                    return false;
                }
                if (!StringComparer.Ordinal.Equals(
                        display.StableDisplayId,
                        displayEvidence.StableDisplayId))
                {
                    failure = MetadataMappingFailureReason.DisplayEvidenceIdentityMismatch;
                    return false;
                }
                if (!TryMapBounds(displayEvidence.Bounds, out var displayBounds))
                {
                    failure = MetadataMappingFailureReason.InvalidPhysicalBounds;
                    return false;
                }

                target = new ResolvedDisplayTarget(display.StableDisplayId, displayBounds!);
                return true;

            case RegionCaptureTarget region:
                if (displayEvidence is not null)
                {
                    failure = MetadataMappingFailureReason.UnexpectedDisplayEvidence;
                    return false;
                }
                if (!RegionCaptureGeometry.TryGetDisplayLocalBounds(region, out _))
                {
                    failure = MetadataMappingFailureReason.InvalidRegionGeometry;
                    return false;
                }
                if (!TryMapBounds(region.PhysicalBounds, out var selectedBounds) ||
                    !TryMapBounds(region.OwningDisplayBounds, out var owningBounds))
                {
                    failure = MetadataMappingFailureReason.InvalidPhysicalBounds;
                    return false;
                }

                target = new ResolvedRegionTarget(
                    region.OwningDisplayId,
                    selectedBounds!,
                    owningBounds!);
                return true;

            default:
                failure = MetadataMappingFailureReason.UnsupportedTargetKind;
                return false;
        }
    }

    private static bool TryMapDevice(
        AdmittedCaptureDevice source,
        out CaptureDeviceSelection? selection,
        out MetadataMappingFailureReason failure)
    {
        selection = null;
        failure = default;

        var role = source.Role switch
        {
            CoreDeviceRole.DesktopAudio => MetadataDeviceRole.DesktopAudio,
            CoreDeviceRole.Microphone => MetadataDeviceRole.Microphone,
            CoreDeviceRole.Webcam => MetadataDeviceRole.Webcam,
            _ => (MetadataDeviceRole?)null,
        };
        if (role is null)
        {
            failure = MetadataMappingFailureReason.UnsupportedDeviceRole;
            return false;
        }

        switch (source.SelectionMode)
        {
            case CoreSelectionMode.Exact:
                if (string.IsNullOrWhiteSpace(source.RequestedDeviceId))
                {
                    failure = MetadataMappingFailureReason.InvalidDeviceIdentity;
                    return false;
                }

                selection = CaptureDeviceSelection.Exact(role.Value, source.RequestedDeviceId);
                return true;

            case CoreSelectionMode.SystemDefault:
                if (role == MetadataDeviceRole.Webcam ||
                    !StringComparer.Ordinal.Equals(
                        source.RequestedDeviceId,
                        AudioDeviceInfo.SystemDefaultDeviceId))
                {
                    failure = MetadataMappingFailureReason.InvalidDeviceIdentity;
                    return false;
                }

                selection = CaptureDeviceSelection.SystemDefault(role.Value);
                return true;

            default:
                failure = MetadataMappingFailureReason.UnsupportedDeviceSelectionMode;
                return false;
        }
    }

    private static MetadataMappingFailureReason? ValidateWebcamAdmission(
        CaptureRequest request,
        CaptureDeviceManifest manifest)
    {
        var admittedWebcam = manifest.Entries.SingleOrDefault(
            entry => entry.Role == CoreDeviceRole.Webcam);
        if (admittedWebcam is null)
            return null;

        if (request.Initiation != CaptureInitiation.Manual ||
            request.Webcam is null ||
            request.Webcam.AuthorizationScope != WebcamCaptureAuthorizationScope.ExplicitManualRecordingOnly ||
            admittedWebcam.SelectionMode != CoreSelectionMode.Exact ||
            !StringComparer.Ordinal.Equals(
                admittedWebcam.RequestedDeviceId,
                request.Webcam.DeviceId))
        {
            return MetadataMappingFailureReason.WebcamAdmissionMismatch;
        }

        return null;
    }

    private static bool HasSamePhysicalIdentity(CaptureTarget left, CaptureTarget right) =>
        (left, right) switch
        {
            (GameCaptureTarget first, GameCaptureTarget second) =>
                StringComparer.Ordinal.Equals(first.GameId, second.GameId) &&
                first.ProcessId == second.ProcessId &&
                StringComparer.Ordinal.Equals(first.ExecutableName, second.ExecutableName) &&
                first.ProcessStartedAt == second.ProcessStartedAt,
            (WindowCaptureTarget first, WindowCaptureTarget second) =>
                first.ProcessId == second.ProcessId &&
                first.WindowHandle == second.WindowHandle &&
                StringComparer.Ordinal.Equals(first.ExecutableName, second.ExecutableName) &&
                StringComparer.Ordinal.Equals(first.WindowClass, second.WindowClass),
            (DisplayCaptureTarget first, DisplayCaptureTarget second) =>
                StringComparer.Ordinal.Equals(first.StableDisplayId, second.StableDisplayId) &&
                first.MonitorIndex == second.MonitorIndex,
            (RegionCaptureTarget first, RegionCaptureTarget second) =>
                StringComparer.Ordinal.Equals(first.OwningDisplayId, second.OwningDisplayId) &&
                first.PhysicalBounds == second.PhysicalBounds &&
                first.OwningDisplayBounds == second.OwningDisplayBounds,
            _ => ReferenceEquals(left, right),
        };

    private static bool TryMapBounds(CoreBounds source, out MetadataBounds? bounds)
    {
        bounds = null;
        if (source.Width <= 0 || source.Height <= 0)
            return false;

        bounds = new MetadataBounds(source.X, source.Y, source.Width, source.Height);
        return true;
    }

    private static MetadataMappingResult<T> Failure<T>(MetadataMappingFailureReason reason)
        where T : class =>
        MetadataMappingResult<T>.Failure(reason);
}
