using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Tests.Audio;

public sealed class AudioDeviceSelectionTests
{
    public static TheoryData<string?> DefaultRequests => new()
    {
        null,
        string.Empty,
        " ",
        "default",
        "DEFAULT",
    };

    [Theory]
    [MemberData(nameof(DefaultRequests))]
    public void EmptyOrDefaultRequestIntentionallySelectsSystemDefault(string? requested)
    {
        var catalog = new[]
        {
            new AudioDeviceInfo("endpoint-1", "Speakers", IsDefault: false),
        };

        var selected = AudioDeviceSelection.PreserveSavedSelection(catalog, requested);
        var resolved = AudioDeviceSelection.RequireExactAvailableDevice(
            requested,
            catalog,
            "desktop/output");

        Assert.True(selected.SelectedDevice.IsDefault);
        Assert.True(selected.SelectedDevice.IsAvailable);
        Assert.Equal(AudioDeviceInfo.SystemDefaultDeviceId, resolved);
    }

    [Fact]
    public void MissingExplicitSelectionRemainsSelectedAndUnavailable()
    {
        var selection = AudioDeviceSelection.PreserveSavedSelection(
            [new AudioDeviceInfo("available-output", "Speakers", IsDefault: false)],
            "saved-missing-output");

        Assert.Equal("saved-missing-output", selection.SelectedDevice.DeviceId);
        Assert.False(selection.SelectedDevice.IsDefault);
        Assert.False(selection.SelectedDevice.IsAvailable);
        Assert.Contains("Unavailable", selection.SelectedDevice.DisplayName, StringComparison.Ordinal);
        Assert.Contains(selection.SelectedDevice, selection.Devices);
    }

    [Fact]
    public void ExplicitEndpointRequiresExactCaseSensitivePresence()
    {
        var catalog = new[]
        {
            new AudioDeviceInfo("Exact-Endpoint-ID", "Speakers", IsDefault: false),
        };

        Assert.Equal(
            "Exact-Endpoint-ID",
            AudioDeviceSelection.RequireExactAvailableDevice(
                "Exact-Endpoint-ID",
                catalog,
                "desktop/output"));
        Assert.Throws<InvalidOperationException>(() =>
            AudioDeviceSelection.RequireExactAvailableDevice(
                "exact-endpoint-id",
                catalog,
                "desktop/output"));
    }

    [Fact]
    public void EndpointPresentOnlyInWrongDirectionIsRejected()
    {
        var outputCatalog = new[]
        {
            new AudioDeviceInfo("speaker-endpoint", "Speakers", IsDefault: false),
        };
        var inputCatalog = new[]
        {
            new AudioDeviceInfo("microphone-endpoint", "Microphone", IsDefault: false),
        };

        Assert.Equal(
            "speaker-endpoint",
            AudioDeviceSelection.RequireExactAvailableDevice(
                "speaker-endpoint",
                outputCatalog,
                "desktop/output"));
        Assert.Throws<InvalidOperationException>(() =>
            AudioDeviceSelection.RequireExactAvailableDevice(
                "speaker-endpoint",
                inputCatalog,
                "microphone/input"));
    }

    [Fact]
    public void DuplicateExplicitEndpointIsRejectedAsAmbiguous()
    {
        var duplicateCatalog = new[]
        {
            new AudioDeviceInfo("duplicate", "First", IsDefault: false),
            new AudioDeviceInfo("duplicate", "Second", IsDefault: false),
        };

        Assert.Throws<InvalidOperationException>(() =>
            AudioDeviceSelection.RequireExactAvailableDevice(
                "duplicate",
                duplicateCatalog,
                "desktop/output"));
    }

    [Theory]
    [MemberData(nameof(DefaultRequests))]
    public void DefaultAdmissionBypassesStrictCatalogRead(string? requested)
    {
        var catalogReads = 0;

        var resolved = ObsCaptureEngine.ResolveAudioDeviceId(
            requested,
            () =>
            {
                catalogReads++;
                throw new InvalidOperationException("must not enumerate");
            },
            "desktop/output",
            CaptureDeviceRole.DesktopAudio);

        Assert.Equal(AudioDeviceInfo.SystemDefaultDeviceId, resolved);
        Assert.Equal(0, catalogReads);
    }

    [Fact]
    public void StrictCatalogFailureIsDistinctFromMissingExactSelection()
    {
        var catalogFailure = Assert.Throws<CaptureDeviceStartupException>(() =>
            ObsCaptureEngine.ResolveAudioDeviceId(
                "speaker",
                () => throw new InvalidDataException("duplicate catalog"),
                "desktop/output",
                CaptureDeviceRole.DesktopAudio));
        var selectionFailure = Assert.Throws<CaptureDeviceStartupException>(() =>
            ObsCaptureEngine.ResolveAudioDeviceId(
                "speaker",
                () => [new AudioDeviceInfo("other", "Other", IsDefault: false)],
                "desktop/output",
                CaptureDeviceRole.DesktopAudio));

        Assert.Equal(CaptureDeviceStartupStage.Catalog, catalogFailure.Stage);
        Assert.Equal(CaptureDeviceStartupReason.InvalidCatalog, catalogFailure.Reason);
        Assert.Equal(CaptureDeviceStartupStage.Selection, selectionFailure.Stage);
        Assert.Equal(CaptureDeviceStartupReason.ExactSelectionUnavailable, selectionFailure.Reason);
    }

    [Fact]
    public void ExplicitSelectionRemainsDirectionBoundAndCaseSensitiveAtAdmission()
    {
        var error = Assert.Throws<CaptureDeviceStartupException>(() =>
            ObsCaptureEngine.ResolveAudioDeviceId(
                "SPEAKER-ID",
                () => [new AudioDeviceInfo("speaker-id", "Speakers", IsDefault: false)],
                "microphone/input",
                CaptureDeviceRole.Microphone));

        Assert.Equal(CaptureDeviceRole.Microphone, error.Role);
        Assert.Equal(CaptureDeviceStartupStage.Selection, error.Stage);
        Assert.Equal(CaptureDeviceStartupReason.ExactSelectionUnavailable, error.Reason);
    }
}
