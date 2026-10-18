using WatchDog.App.ViewModels;
using System.Runtime.CompilerServices;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;
using WatchDog.Native.Win32;

namespace WatchDog.Integration.Tests;

public sealed class TutorialWorkspacePresentationTests
{
    [Fact]
    public void CurrentOfferedWindowFreezesExactMetadataAndSessionPlanWithoutDispatch()
    {
        var settings = new AppSettings
        {
            Recording = new SessionRecordingConfig { Mode = RecordingMode.ReplayBufferOnly, FileFormat = "mkv", SegmentDurationMinutes = 17 },
            Audio = new AudioMixConfig { DesktopDeviceId = "system-device", MicDeviceId = "mic-device", SeparateAudioTracks = true },
            Storage = new StorageSettings { SavePath = @"C:\Tutorial Output" },
            Buffer = new WatchDog.Core.Buffer.BufferConfig { OutputDirectory = @"C:\Tutorial Output" },
        };
        var observation = new FakeObservation();
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), observation);
        var offered = Window(17, 0x1717, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([offered]);
        vm.SelectedWindow = offered;
        vm.ConfirmWindowCommand.Execute(null);

        var plan = Assert.IsType<WatchDog.Core.Tutorial.TutorialRecordingPlan>(vm.Plan);
        var target = Assert.IsType<WindowCaptureTarget>(plan.Request.Target);
        Assert.Equal(offered.ProcessId, target.ProcessId);
        Assert.Equal(offered.Handle, target.WindowHandle);
        Assert.Equal(offered.Title, target.WindowTitle);
        Assert.Equal(offered.ExecutableName, target.ExecutableName);
        Assert.Equal(offered.ClassName, target.WindowClass);
        Assert.Equal(CaptureInitiation.Manual, plan.Request.Initiation);
        Assert.Equal(RecordingMode.SessionRecording, plan.Request.StartPolicy!.Recording.Mode);
        Assert.Equal(RecordingMode.ReplayBufferOnly, settings.Recording.Mode);
        Assert.Contains("0x1717", vm.ExactWindowText);
        Assert.Contains("LessonClass", vm.ExactWindowText);
        Assert.Contains("system-device", vm.AudioText);
        Assert.Contains("mic-device", vm.AudioText);
        Assert.Contains("separate tracks", vm.AudioText);
        Assert.Contains("mkv", vm.OutputText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("17", vm.OutputText);
        Assert.Contains(@"C:\Tutorial Output", vm.OutputText);
        Assert.False(vm.StartAvailable);
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.StartCommand.Execute(null);
        Assert.Same(plan, vm.Plan);
        Assert.Equal(0, observation.MutationCount);
    }

    [Fact]
    public void RefreshAndReplacementRejectStaleOrCopiedWindowRecords()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(new AppSettings()), new FakeObservation());
        var old = Window(17, 0x1717, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([old]);
        vm.SelectedWindow = old;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.NotNull(vm.Plan);

        var replacement = Window(18, 0x1818, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([replacement]);
        Assert.Null(vm.Plan);
        Assert.Null(vm.SelectedWindow);
        vm.SelectedWindow = old;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        vm.SelectedWindow = replacement with { };
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        vm.SelectedWindow = replacement;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Equal(replacement.Handle, Assert.IsType<WindowCaptureTarget>(vm.Plan!.Request.Target).WindowHandle);
        vm.SelectedWindow = null;
        Assert.Null(vm.Plan);
        vm.SelectedWindow = replacement;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.NotNull(vm.Plan);
        vm.SelectedWindow = old;
        Assert.Null(vm.Plan);
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.Contains("stale", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighlightingAnotherOfferedWindowInvalidatesEarlierPlanUntilConfirmation()
    {
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(new AppSettings()), new FakeObservation());
        var first = Window(17, 0x1717, "First", "first.exe", "FirstClass");
        var second = Window(18, 0x1818, "Second", "second.exe", "SecondClass");
        vm.OfferWindows([first, second]);
        vm.SelectedWindow = first;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Contains("First", vm.BoundaryText);
        vm.SelectedWindow = second;
        Assert.Null(vm.Plan);
        Assert.DoesNotContain("First", vm.BoundaryText);
        Assert.Equal("No exact window selected.", vm.ExactWindowText);
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Contains("Second", vm.BoundaryText);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("audio")]
    [InlineData("encoder")]
    [InlineData("audio-null")]
    public void MissingOrInvalidStartupBaselineCannotProduceAPlan(string caseName)
    {
        AppSettings? settings = caseName switch
        {
            "missing" => null,
            "audio" => new AppSettings { Audio = new AudioMixConfig { MicVolume = -1 } },
            "audio-null" => new AppSettings { Audio = null! },
            _ => new AppSettings { Capture = new CaptureConfig { Encoder = EncoderType.AmfH264 } },
        };
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), new FakeObservation());
        var window = Window(17, 0x1717, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan);
        Assert.False(vm.StartAvailable);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, vm.StatusText);
        Assert.DoesNotContain("ready", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CameraRemainsOffWithoutFreshConsentAndDisplayRegionRemainUnavailable()
    {
        var settings = new AppSettings
        {
            Webcam = new WatchDog.Core.Webcam.WebcamConfig
            {
                EnabledForExplicitManualRecording = true,
                DeviceId = "saved-camera",
                DisplayName = "Saved Camera",
            },
        };
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(settings), new FakeObservation());
        var window = Window(17, 0x1717, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.Null(vm.Plan!.Request.Webcam);
        Assert.Contains("off", vm.CameraText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.CameraOptions);
        Assert.False(vm.IncludeCamera);
        Assert.Contains("unavailable", vm.DisplayAvailabilityText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", vm.RegionAvailabilityText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BusyClosedAndUnrelatedObservationsNeverEnableStartOrClaimPlan()
    {
        var observation = new FakeObservation();
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(new AppSettings()), observation);
        var window = Window(17, 0x1717, "Lesson", "lesson.exe", "LessonClass");
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        var plan = vm.Plan!;
        observation.Publish(new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Starting,
            CaptureReadiness.Busy, CaptureHealth.Healthy, null, null));
        Assert.Contains("pending", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.StartAvailable);
        var unrelated = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, new GameCaptureTarget("game", 99, "game.exe"));
        var context = new CaptureSessionContext(Guid.NewGuid(), 2, unrelated, unrelated.Target, unrelated.Target);
        observation.Publish(new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Active,
            CaptureReadiness.Available, CaptureHealth.Healthy, context, null));
        Assert.Contains("Another capture", vm.StatusText);
        Assert.Equal(plan.Request.Target, vm.Plan!.Request.Target);
        observation.Publish(new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Shutdown,
            CaptureReadiness.Closed, CaptureHealth.Healthy, null, null));
        Assert.Contains("closed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.StartCommand.Execute(null);
        Assert.Equal(0, observation.MutationCount);
    }

    [Fact]
    public void DisposalUnsubscribesAndIgnoresLaterObservationChanges()
    {
        var observation = new FakeObservation();
        var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(new AppSettings()), observation);
        Assert.Equal(1, observation.SubscriberCount);
        var prior = vm.StatusText;
        vm.Dispose();
        Assert.Equal(0, observation.SubscriberCount);
        observation.Publish(new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Shutdown,
            CaptureReadiness.Closed, CaptureHealth.Healthy, null, null));
        Assert.Equal(prior, vm.StatusText);
    }

    [Fact]
    public void BackendFailureDetailsAreNotAnnouncedInTutorialStatus()
    {
        var observation = new FakeObservation();
        using var vm = new TutorialWorkspaceViewModel(CaptureStartupBaselineResult.Create(new AppSettings()), observation);
        observation.Publish(new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Faulted,
            CaptureReadiness.Available, CaptureHealth.Failed, null,
            new CaptureFailure(CaptureFailureKind.Unexpected, "capture.backend.unexpected",
                @"C:\Users\private\capture.mp4 device-opaque-123")));

        Assert.Contains("warning", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\private", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("device-opaque-123", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceSourceBindsLiveStatusAndKeyboardConfirmationWithoutEnabledStart()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(projectRoot, "src", "WatchDog.App", "Views", "TutorialWorkspaceWindow.xaml"));
        Assert.Contains("AutomationProperties.Name=\"{Binding StatusText", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("KeyDown=\"OnWindowOptionsKeyDown\"", xaml);
        Assert.Contains("PreviewKeyDown=\"OnPreviewKeyDown\"", xaml);
        Assert.Contains("Command=\"{Binding StartCommand}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding StartAvailable}\"", xaml);
    }

    private static string SourceFile([CallerFilePath] string sourceFile = "") => sourceFile;

    private static WindowEnumerator.WindowInfo Window(int pid, long handle, string title, string executable, string windowClass) =>
        new(new IntPtr(handle), title, executable, pid, windowClass);

    private sealed class FakeObservation : ITutorialLifecycleObservation
    {
        private Action<CaptureLifecycleSnapshot>? _changed;
        public CaptureLifecycleSnapshot Snapshot { get; private set; } = CaptureLifecycleSnapshot.Initial;
        public int SubscriberCount { get; private set; }
        public int MutationCount { get; private set; }
        public event Action<CaptureLifecycleSnapshot>? SnapshotChanged
        {
            add { _changed += value; SubscriberCount++; }
            remove { _changed -= value; SubscriberCount--; }
        }
        public void Publish(CaptureLifecycleSnapshot snapshot)
        {
            Snapshot = snapshot;
            _changed?.Invoke(snapshot);
        }
    }
}
