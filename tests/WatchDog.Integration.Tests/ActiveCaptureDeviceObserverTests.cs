using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.App.Services;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Webcam;

namespace WatchDog.Integration.Tests;

public sealed class ActiveCaptureDeviceObserverTests
{
    [Fact]
    public async Task SystemDefault_FirstSnapshotEstablishesBaselineAndKnownChangeStops()
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.DesktopAudio),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var audio = new AudioDevices
        {
            Output = QueueSnapshots(
                AudioSnapshot(AudioEndpointDirection.Output, "speaker-a", "speaker-a", "speaker-b"),
                AudioSnapshot(AudioEndpointDirection.Output, "speaker-b", "speaker-a", "speaker-b")),
        };
        await using var observer = Observer(lifecycle, audio, new WebcamDevices());

        await observer.PollOnceAsync();
        Assert.Empty(backend.StopCalls);

        await observer.PollOnceAsync();

        Assert.Single(backend.StopCalls);
        Assert.Equal(
            "capture.device.desktop-audio.active.default-changed",
            lifecycle.Snapshot.LastFailure?.Code);
        Assert.Contains("Windows changed the default", lifecycle.Snapshot.LastFailure?.Message);
        Assert.Contains("could not verify that capture stayed on the same device", lifecycle.Snapshot.LastFailure?.Message);
        Assert.DoesNotContain("speaker-a", lifecycle.Snapshot.LastFailure?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("speaker-b", lifecycle.Snapshot.LastFailure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitSelectionIgnoresUnrelatedDefaultChangesAndDisabledRolesAreNotQueried()
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker-a"),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var audio = new AudioDevices
        {
            Output = QueueSnapshots(
                AudioSnapshot(AudioEndpointDirection.Output, "speaker-a", "speaker-a", "speaker-b"),
                AudioSnapshot(AudioEndpointDirection.Output, "speaker-b", "speaker-a", "speaker-b")),
        };
        var webcams = new WebcamDevices();
        await using var observer = Observer(lifecycle, audio, webcams);

        await observer.PollOnceAsync();
        await observer.PollOnceAsync();

        Assert.Empty(backend.StopCalls);
        Assert.Equal(2, audio.OutputReads);
        Assert.Equal(0, audio.InputReads);
        Assert.Equal(0, webcams.Reads);
    }

    [Fact]
    public async Task MissingInitialDefaultFailsClosedWithoutClaimingObsSwitched()
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.Microphone),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var audio = new AudioDevices
        {
            Input = () => AudioSnapshot(AudioEndpointDirection.Input, null, "mic-a"),
        };
        await using var observer = Observer(lifecycle, audio, new WebcamDevices());

        await observer.PollOnceAsync();

        Assert.Equal(
            "capture.device.microphone.active.default-unavailable",
            lifecycle.Snapshot.LastFailure?.Code);
        Assert.DoesNotContain("OBS switched", lifecycle.Snapshot.LastFailure?.Message ?? string.Empty);
    }

    [Theory]
    [InlineData(true, "catalog-invalid")]
    [InlineData(false, "catalog-unavailable")]
    public async Task CatalogFailuresRemainDistinctAndIdentityBounded(bool invalid, string suffix)
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker-a"),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var audio = new AudioDevices
        {
            Output = () => throw (invalid
                ? new InvalidDataException("bad catalog")
                : new InvalidOperationException("unavailable")),
        };
        await using var observer = Observer(lifecycle, audio, new WebcamDevices());

        await observer.PollOnceAsync();

        Assert.EndsWith(suffix, lifecycle.Snapshot.LastFailure?.Code);
        Assert.Contains("unobservable", lifecycle.Snapshot.LastFailure?.Message);
        Assert.DoesNotContain("disappeared", lifecycle.Snapshot.LastFailure?.Message ?? string.Empty);
    }

    [Fact]
    public async Task ExactWebcamDisappearanceUsesPhysicalMetadataOnly()
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "camera-a"),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var webcams = new WebcamDevices
        {
            Devices = [new WebcamDeviceInfo("camera-b", "Other camera")],
        };
        await using var observer = Observer(lifecycle, new AudioDevices(), webcams);

        await observer.PollOnceAsync();

        Assert.Equal(
            "capture.device.webcam.active.selection-unavailable",
            lifecycle.Snapshot.LastFailure?.Code);
    }

    [Fact]
    public async Task StopAsyncDrainsAnInFlightMetadataRead()
    {
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker-a"),
        ]));
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var releaseRead = new ManualResetEventSlim();
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = new AudioDevices
        {
            Output = () =>
            {
                readEntered.TrySetResult();
                releaseRead.Wait();
                return AudioSnapshot(AudioEndpointDirection.Output, "speaker-a", "speaker-a");
            },
        };
        var ticks = new ManualTickSource();
        await using var observer = new ActiveCaptureDeviceObserver(
            lifecycle,
            audio,
            new WebcamDevices(),
            NullLogger<ActiveCaptureDeviceObserver>.Instance,
            ticks,
            new FixedTimeProvider());
        await observer.StartAsync(CancellationToken.None);
        ticks.Tick();
        await readEntered.Task;

        var stop = observer.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);
        releaseRead.Set();
        await stop;

        Assert.Empty(backend.StopCalls);
    }

    [Fact]
    public async Task DeviceLossObservedWhileSavingWaitsForSaveBeforeBackendStop()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var backend = new Backend(new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a"),
        ]))
        {
            SaveHandler = async (_, _) =>
            {
                order.Add("save-entered");
                saveEntered.SetResult();
                await releaseSave.Task;
                order.Add("save-completed");
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
            StopHandler = (context, _) =>
            {
                order.Add("stop");
                return Task.FromResult(CaptureBackendCommandResult.Success());
            },
        };
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        await StartAsync(lifecycle);
        var audio = new AudioDevices
        {
            Input = () => AudioSnapshot(AudioEndpointDirection.Input, null),
        };
        await using var observer = Observer(lifecycle, audio, new WebcamDevices());

        var save = lifecycle.SaveAsync();
        await saveEntered.Task;
        var poll = observer.PollOnceAsync();
        Assert.False(poll.IsCompleted);
        Assert.Empty(backend.StopCalls);

        releaseSave.SetResult();
        Assert.True((await save).Saved);
        await poll;

        Assert.Equal(["save-entered", "save-completed", "stop"], order);
    }

    private static ActiveCaptureDeviceObserver Observer(
        CaptureLifecycleCoordinator lifecycle,
        IAudioDeviceEnumerator audio,
        IWebcamDeviceEnumerator webcams) =>
        new(
            lifecycle,
            audio,
            webcams,
            NullLogger<ActiveCaptureDeviceObserver>.Instance,
            new ManualTickSource(),
            new FixedTimeProvider());

    private static async Task StartAsync(CaptureLifecycleCoordinator lifecycle)
    {
        var request = new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic,
            new GameCaptureTarget("game", 10, "game.exe", DateTimeOffset.UnixEpoch));
        Assert.True((await lifecycle.StartAsync(request)).Succeeded);
    }

    private static AudioDeviceCatalogSnapshot AudioSnapshot(
        AudioEndpointDirection direction,
        string? defaultId,
        params string[] ids) =>
        AudioDeviceCatalogSnapshot.CreateValidated(
            direction,
            WindowsAudioEndpointRole.Multimedia,
            ids.Select(id => new AudioDeviceInfo(id, id, IsDefault: false)),
            defaultId);

    private static Func<AudioDeviceCatalogSnapshot> QueueSnapshots(
        params AudioDeviceCatalogSnapshot[] snapshots)
    {
        var queue = new Queue<AudioDeviceCatalogSnapshot>(snapshots);
        return queue.Dequeue;
    }

    private sealed class Backend(CaptureDeviceManifest manifest) : ICaptureLifecycleBackend
    {
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendCommandResult>>? StopHandler { get; init; }
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendSaveResult>>? SaveHandler { get; init; }
        public List<CaptureSessionContext> StopCalls { get; } = [];

        public Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget, manifest));

        public Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls.Add(context);
            return StopHandler?.Invoke(context, cancellationToken) ??
                   Task.FromResult(CaptureBackendCommandResult.Success());
        }

        public Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            SaveHandler?.Invoke(context, cancellationToken) ??
            Task.FromResult(CaptureBackendSaveResult.Success("clip.mp4"));
    }

    private sealed class AudioDevices : IAudioDeviceEnumerator
    {
        public Func<AudioDeviceCatalogSnapshot>? Output { get; init; }
        public Func<AudioDeviceCatalogSnapshot>? Input { get; init; }
        public int OutputReads { get; private set; }
        public int InputReads { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];
        public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];
        public IReadOnlyList<AudioDeviceInfo> GetOutputDevicesOrThrow() => [];
        public IReadOnlyList<AudioDeviceInfo> GetInputDevicesOrThrow() => [];

        public AudioDeviceCatalogSnapshot GetOutputSnapshotOrThrow()
        {
            OutputReads++;
            return Output?.Invoke() ?? AudioSnapshot(AudioEndpointDirection.Output, null);
        }

        public AudioDeviceCatalogSnapshot GetInputSnapshotOrThrow()
        {
            InputReads++;
            return Input?.Invoke() ?? AudioSnapshot(AudioEndpointDirection.Input, null);
        }
    }

    private sealed class WebcamDevices : IWebcamDeviceEnumerator
    {
        public IReadOnlyList<WebcamDeviceInfo> Devices { get; init; } = [];
        public int Reads { get; private set; }

        public IReadOnlyList<WebcamDeviceInfo> GetPhysicalDevicesOrThrow()
        {
            Reads++;
            return Devices;
        }
    }

    private sealed class ManualTickSource : IActiveCaptureDeviceTickSource
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();

        public void Tick() => _ticks.Writer.TryWrite(true);

        public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
            _ticks.Reader.ReadAsync(cancellationToken);

        public void Dispose() => _ticks.Writer.TryComplete();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
