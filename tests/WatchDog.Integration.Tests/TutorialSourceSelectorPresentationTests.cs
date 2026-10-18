using System.Runtime.CompilerServices;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;
using WatchDog.Native.Win32;

namespace WatchDog.Integration.Tests;

public sealed class TutorialSourceSelectorPresentationTests
{
    [Fact]
    public void RegionNeedsExplicitCompleteCoordinatesAndPreservesResolvedTarget()
    {
        var calls = 0;
        RegionCaptureTarget? resolved = null;
        var selection = new TutorialRegionSelection(_ => true, (id, bounds) =>
        {
            calls++;
            return resolved = new(id, bounds, new(-1920, -100, 1920, 1080));
        }, _ => true);
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            isDisplayCurrent: _ => true, regionSelection: selection);
        Assert.Equal(string.Empty, vm.RegionXText);
        Assert.Equal(string.Empty, vm.RegionWidthText);
        PrepareRegion(vm, Display("owner"));
        Assert.Null(vm.Plan);
        Assert.Equal(0, calls);
        vm.ConfirmRegionCommand.Execute(null);
        Assert.Same(resolved, vm.Plan!.Request.Target);
        Assert.Equal(1, calls);
        Assert.Null(vm.Plan.Request.Webcam);
        vm.IncludeCamera = true;
        Assert.False(vm.IncludeCamera);
        Assert.False(vm.StartAvailable);

        vm.RegionXText = "-";
        Assert.Null(vm.Plan);
        vm.ConfirmRegionCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.Equal(1, calls);
        Assert.Contains("-", vm.RegionXText);
    }

    [Theory]
    [InlineData("text-aba")]
    [InlineData("refresh-same-owner")]
    [InlineData("newer-plan")]
    [InlineData("newer-plan-then-throw")]
    [InlineData("same-draft")]
    [InlineData("same-draft-then-throw")]
    public void ReentrantRegionResolverCannotPublishStaleIntentOrEraseNewerConfirmation(string change)
    {
        TutorialWorkspaceViewModel? vm = null;
        var owner = Display("owner");
        var changed = false;
        TutorialRecordingPlan? newerPlan = null;
        var selection = new TutorialRegionSelection(_ => true, (id, bounds) =>
        {
            if (!changed)
            {
                changed = true;
                if (change == "text-aba")
                {
                    var original = vm!.RegionXText;
                    vm.RegionXText = "-1800";
                    vm.RegionXText = original;
                }
                else if (change == "refresh-same-owner")
                {
                    PrepareRegion(vm!, owner);
                }
                else
                {
                    if (!change.StartsWith("same-draft", StringComparison.Ordinal)) vm!.RegionXText = "-1800";
                    vm!.ConfirmRegionCommand.Execute(null);
                    newerPlan = vm.Plan;
                    if (change.EndsWith("then-throw", StringComparison.Ordinal)) throw new InvalidOperationException("private path");
                }
            }
            return new(id, bounds, new(-1920, -100, 1920, 1080));
        }, _ => true);
        using (vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            isDisplayCurrent: _ => true, regionSelection: selection))
        {
            PrepareRegion(vm, owner);
            vm.ConfirmRegionCommand.Execute(null);
            Assert.True(changed);
            if (change.StartsWith("newer-plan", StringComparison.Ordinal) || change.StartsWith("same-draft", StringComparison.Ordinal))
            {
                Assert.NotNull(newerPlan);
                Assert.Same(newerPlan, vm.Plan);
                Assert.Equal(change.StartsWith("same-draft", StringComparison.Ordinal) ? -1900 : -1800,
                    Assert.IsType<RegionCaptureTarget>(vm.Plan!.Request.Target).PhysicalBounds.X);
            }
            else
                Assert.Null(vm.Plan);
            Assert.DoesNotContain("private path", vm.StatusText);
        }
    }

    [Fact]
    public void WindowAndDisplayChangesClearRegionDraftWithoutRestoringCameraConsent()
    {
        var selection = new TutorialRegionSelection(_ => true,
            (id, bounds) => new(id, bounds, new(-1920, -100, 1920, 1080)), _ => true);
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            _ => true, isDisplayCurrent: _ => true, regionSelection: selection);
        PrepareRegion(vm, Display("owner"));
        vm.ConfirmRegionCommand.Execute(null);
        Assert.IsType<RegionCaptureTarget>(vm.Plan!.Request.Target);
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        Assert.Equal(string.Empty, vm.RegionXText);
        Assert.False(vm.CanConfirmRegion);
        Assert.Null(vm.Plan!.Request.Webcam);
        ConfirmDisplay(vm, Display("different-owner"));
        Assert.Equal(string.Empty, vm.RegionWidthText);
        Assert.Null(vm.Plan!.Request.Webcam);
        vm.ConfirmRegionCommand.Execute(null);
        Assert.IsNotType<RegionCaptureTarget>(vm.Plan?.Request.Target);
    }

    private static void PrepareRegion(TutorialWorkspaceViewModel vm, DisplayCaptureOption owner)
    {
        vm.OfferDisplays([owner]);
        vm.SelectedDisplay = owner;
        vm.RegionXText = "-1900";
        vm.RegionYText = "0";
        vm.RegionWidthText = "100";
        vm.RegionHeightText = "100";
    }

    [Fact]
    public void DisplaySelectionRequiresFreshOfferedInstanceAndExplicitConfirmation()
    {
        var validations = 0;
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            isDisplayCurrent: _ => { validations++; return true; });
        Assert.Empty(vm.DisplayOptions);
        Assert.Equal(0, validations);
        var display = Display("stable-a", 7);
        vm.OfferDisplays([display]);
        vm.SelectedDisplay = display;
        Assert.Null(vm.Plan);
        Assert.Equal(0, validations);
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.Same(display.Target, vm.Plan!.Request.Target);
        Assert.Equal(7, ((DisplayCaptureTarget)vm.Plan.Request.Target).MonitorIndex);
        Assert.Equal(CaptureInitiation.Manual, vm.Plan.Request.Initiation);
        Assert.Null(vm.Plan.Request.Webcam);
        Assert.False(vm.StartAvailable);

        var replacement = display with { };
        vm.OfferDisplays([replacement]);
        foreach (var stale in new[] { display, replacement with { } })
        {
            vm.SelectedDisplay = stale;
            vm.ConfirmDisplayCommand.Execute(null);
            Assert.Null(vm.Plan);
        }
        vm.SelectedDisplay = replacement;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.Same(replacement.Target, vm.Plan!.Request.Target);
    }

    [Fact]
    public void DisplayCatalogFailureRevokesEveryPriorChoiceBeforeReportingFailure()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            isDisplayCurrent: _ => true);
        var display = Display("stable-a");
        ConfirmDisplay(vm, display);
        Assert.Throws<InvalidDataException>(() => vm.OfferDisplays([display, display with { Width = 0 }]));
        Assert.Empty(vm.DisplayOptions);
        Assert.Null(vm.SelectedDisplay);
        Assert.Null(vm.Plan);
        ConfirmDisplay(vm, display);
        vm.ReportDisplayListUnavailable();
        Assert.Empty(vm.DisplayOptions);
        Assert.Null(vm.Plan);
        Assert.Contains("unavailable", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        vm.OfferDisplays([]);
        Assert.Empty(vm.DisplayOptions);
        Assert.Null(vm.Plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayConfirmationFailsClosedWhenRevalidationFails(bool throws)
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            isDisplayCurrent: _ => throws ? throw new InvalidOperationException(@"C:\private\display") : false);
        var display = Display("stable-a");
        vm.OfferDisplays([display]);
        vm.SelectedDisplay = display;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.False(vm.StartAvailable);
        Assert.DoesNotContain(@"C:\private", vm.StatusText);
    }

    [Fact]
    public void DisplayWithoutValidatorCannotBeConfirmed()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation());
        var display = Display("stable-a");
        vm.OfferDisplays([display]);
        vm.SelectedDisplay = display;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.Null(vm.Plan);
    }

    [Fact]
    public void CrossSourceSelectionAndRefreshRevokeWindowCameraConsent()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            _ => true, isDisplayCurrent: _ => true);
        var window = Window(17, 0x1717, "Lesson");
        ConfirmWindow(vm, window);
        var camera = new WebcamDeviceInfo("camera-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.NotNull(vm.Plan!.Request.Webcam);

        var display = Display("stable-a");
        vm.OfferDisplays([display]);
        Assert.Null(vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.Null(vm.SelectedCamera);
        Assert.Empty(vm.CameraOptions);
        vm.SelectedDisplay = display;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.NotNull(vm.Plan);
        vm.IncludeCamera = true;
        Assert.False(vm.IncludeCamera);
        Assert.False(vm.CanUseCameraOptions);
        Assert.False(vm.CanConfirmWindow);
        vm.ConfirmWindowCommand.Execute(null);
        Assert.IsNotType<WindowCaptureTarget>(vm.Plan?.Request.Target);

        ConfirmWindow(vm, window);
        Assert.False(vm.CanConfirmDisplay);
        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.False(vm.IncludeCamera);
    }

    private static DisplayCaptureOption Display(string id, int index = 1) =>
        new(new(id, "Display", index), $"DEVICE-{id}", 1920, 1080, true);

    private static void ConfirmDisplay(TutorialWorkspaceViewModel vm, DisplayCaptureOption display)
    {
        vm.OfferDisplays([display]);
        vm.SelectedDisplay = display;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.NotNull(vm.Plan);
    }

    [Fact]
    public void SavedCameraPreferenceNeverBecomesConsentOrEnumeratesAutomatically()
    {
        var settings = Settings() with
        {
            Webcam = new WebcamConfig
            {
                EnabledForExplicitManualRecording = true,
                DeviceId = "saved-camera",
                DisplayName = "Saved Camera",
            },
        };
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), new FakeObservation());
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));

        Assert.False(vm.IncludeCamera);
        Assert.Empty(vm.CameraOptions);
        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.Contains("off", vm.CameraText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.StartAvailable);
        Assert.Contains(@"C:\Tutorial Output", vm.OutputText);
    }

    [Fact]
    public void FreshExactCameraConfirmationFreezesManualPlanWithoutChangingSettings()
    {
        var settings = Settings();
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), new FakeObservation());
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var originalTarget = vm.Plan!.Request.Target;
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;

        Assert.Null(vm.Plan!.Request.Webcam); // Highlighting is not consent.
        vm.ConfirmCameraCommand.Execute(null);

        var request = vm.Plan!.Request;
        Assert.Equal(originalTarget, request.Target);
        Assert.Equal(CaptureInitiation.Manual, request.Initiation);
        Assert.Equal("opaque-a", request.Webcam!.DeviceId);
        Assert.Equal(settings.Webcam.Width, request.Webcam.Width);
        Assert.False(settings.Webcam.EnabledForExplicitManualRecording);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SameLabelDifferentCameraIdAndCopiedOldRecordNeverSubstitute()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation());
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var old = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([old]);
        vm.SelectedCamera = old;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.Equal("opaque-a", vm.Plan!.Request.Webcam!.DeviceId);

        var replacement = new WebcamDeviceInfo("opaque-b", "Camera");
        vm.OfferCameras([replacement]);
        Assert.Null(vm.SelectedCamera);
        Assert.Null(vm.Plan!.Request.Webcam);
        vm.SelectedCamera = old;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.Null(vm.Plan!.Request.Webcam);
        vm.SelectedCamera = replacement with { };
        vm.ConfirmCameraCommand.Execute(null);
        Assert.Null(vm.Plan!.Request.Webcam);
        vm.SelectedCamera = replacement;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.Equal("opaque-b", vm.Plan!.Request.Webcam!.DeviceId);
    }

    [Fact]
    public void MissingOrMalformedCameraCatalogRevokesPriorConsent()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation());
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);

        vm.OfferCameras([]);
        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.Null(vm.SelectedCamera);
        Assert.Throws<InvalidDataException>(() => vm.OfferCameras([
            camera, new WebcamDeviceInfo("opaque-a", "Another label")]));
        Assert.Empty(vm.CameraOptions);
        Assert.Null(vm.Plan!.Request.Webcam);
        vm.ReportCameraListUnavailable();
        Assert.Contains("unavailable", vm.CameraText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowDisappearanceAndSourceChangeFailClosedAndRevokeCamera()
    {
        var available = true;
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            window => available && window.ProcessId == 17);
        var first = Window(17, 0x1717, "Lesson");
        ConfirmWindow(vm, first);
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.NotNull(vm.Plan!.Request.Webcam);

        var second = Window(18, 0x1818, "Lesson");
        vm.OfferWindows([second]);
        Assert.Null(vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.Null(vm.SelectedCamera);
        vm.SelectedWindow = second;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.Contains("unavailable", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        available = false;
        vm.OfferWindows([first]);
        vm.SelectedWindow = first;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.DoesNotContain("desktop", vm.BoundaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidRequestedCameraFormatAndRevocationNeverDispatchCapture()
    {
        var observation = new FakeObservation();
        var settings = Settings() with { Webcam = new WebcamConfig { Width = 0 } };
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), observation);
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.Contains("camera", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        vm.IncludeCamera = false;
        vm.StartCommand.Execute(null);
        Assert.Equal(0, observation.MutationCount);
        Assert.False(vm.StartAvailable);
        Assert.Contains("unavailable", vm.DisplayAvailabilityText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", vm.RegionAvailabilityText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedOrDuplicateWindowCatalogInvalidatesPriorPlan()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation());
        var valid = Window(17, 0x1717, "Lesson");
        ConfirmWindow(vm, valid);

        Assert.Throws<InvalidDataException>(() => vm.OfferWindows([valid, valid with { Title = "Changed" }]));
        Assert.Null(vm.Plan);
        Assert.Empty(vm.WindowOptions);
        Assert.Throws<InvalidDataException>(() => vm.OfferWindows([Window(0, 0, "Invalid")]));
        Assert.Null(vm.Plan);
    }

    [Fact]
    public void UncheckingCameraAfterConfirmationRemovesItFromFrozenPlan()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation());
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);
        var withCamera = vm.Plan!.Request;
        Assert.NotNull(withCamera.Webcam);

        vm.IncludeCamera = false;

        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.NotEqual(withCamera.RequestId, vm.Plan.Request.RequestId);
        Assert.Empty(vm.CameraOptions);
        Assert.False(vm.CanConfirmCamera);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecheckingLostWindowRevokesExistingCameraPlan(bool throws)
    {
        var available = true;
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(Settings()), new FakeObservation(),
            _ => throws && !available ? throw new InvalidOperationException("private path") : available);
        ConfirmWindow(vm, Window(17, 0x1717, "Lesson"));
        var camera = new WebcamDeviceInfo("opaque-a", "Camera");
        vm.IncludeCamera = true;
        vm.OfferCameras([camera]);
        vm.SelectedCamera = camera;
        vm.ConfirmCameraCommand.Execute(null);
        Assert.NotNull(vm.Plan!.Request.Webcam);

        available = false;
        vm.ConfirmCameraCommand.Execute(null);

        Assert.Null(vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.Null(vm.SelectedCamera);
        Assert.Empty(vm.CameraOptions);
        Assert.Contains("window", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private path", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void CameraUiHasExplicitMetadataRefreshAndNoProductionStart()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "WatchDog.App", "Views", "TutorialWorkspaceWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "WatchDog.App", "Views", "TutorialWorkspaceWindow.xaml.cs"));
        Assert.Contains("IsChecked=\"{Binding IncludeCamera, Mode=TwoWay}\"", xaml);
        Assert.Contains("Click=\"OnRefreshCamerasClick\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedCamera, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding DeviceId}\"", xaml);
        Assert.Contains("Path=(ItemsControl.AlternationIndex)", xaml);
        Assert.Contains("<ListBox.ItemContainerStyle>", xaml);
        Assert.Contains("<Setter Property=\"AutomationProperties.Name\">", xaml);
        Assert.Contains("StringFormat=\"{}{0} (camera option {1})\"", xaml);
        Assert.Contains("Command=\"{Binding ConfirmCameraCommand}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding StartAvailable}\"", xaml);
        Assert.Contains("_viewModel.OfferCameras(_enumerateCameras())", code);
        var loaded = code[code.IndexOf("private void OnLoaded", StringComparison.Ordinal)..code.IndexOf("private void OnRefreshWindowsClick", StringComparison.Ordinal)];
        Assert.DoesNotContain("_enumerateCameras", loaded);
    }

    [Fact]
    public void SameTitleWindowRowsShowExactDiscriminatorsBeforeConfirmation()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "WatchDog.App", "Views", "TutorialWorkspaceWindow.xaml"));
        Assert.DoesNotContain("DisplayMemberPath=\"Title\"", xaml);
        Assert.Contains("<ListBox.ItemTemplate>", xaml);
        Assert.Contains("<Binding Path=\"ExecutableName\"/>", xaml);
        Assert.Contains("<Binding Path=\"ProcessId\"/>", xaml);
        Assert.Contains("<Binding Path=\"Handle\"/>", xaml);
        Assert.Contains("StringFormat=\"{}{0} | {1}, PID {2}, HWND {3}\"", xaml);
    }

    private static string SourceFile([CallerFilePath] string sourceFile = "") => sourceFile;

    private static AppSettings Settings() => new()
    {
        Storage = new StorageSettings { SavePath = @"C:\Tutorial Output" },
        Buffer = new WatchDog.Core.Buffer.BufferConfig { OutputDirectory = @"C:\Tutorial Output" },
    };

    private static WindowEnumerator.WindowInfo Window(int pid, long handle, string title) =>
        new(new IntPtr(handle), title, "lesson.exe", pid, "LessonClass");

    private static void ConfirmWindow(TutorialWorkspaceViewModel vm, WindowEnumerator.WindowInfo window)
    {
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.NotNull(vm.Plan);
    }

    private sealed class FakeObservation : ITutorialLifecycleObservation
    {
        public CaptureLifecycleSnapshot Snapshot => CaptureLifecycleSnapshot.Initial;
        public int MutationCount { get; private set; }
        public event Action<CaptureLifecycleSnapshot>? SnapshotChanged { add { } remove { } }
    }
}
