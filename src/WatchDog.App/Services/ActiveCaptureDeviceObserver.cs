using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Webcam;

namespace WatchDog.App.Services;

/// <summary>
/// Sequentially observes only admitted device identity metadata for the currently active capture.
/// It does not inspect media samples, infer native errors, or rebind/substitute a device.
/// </summary>
public sealed class ActiveCaptureDeviceObserver : IHostedService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly CaptureLifecycleCoordinator _lifecycle;
    private readonly IAudioDeviceEnumerator _audioDevices;
    private readonly IWebcamDeviceEnumerator _webcamDevices;
    private readonly ILogger<ActiveCaptureDeviceObserver> _logger;
    private readonly IActiveCaptureDeviceTickSource _ticks;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<DeviceBaselineKey, string> _defaultBaselines = [];
    private readonly object _lifetimeGate = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public ActiveCaptureDeviceObserver(
        CaptureLifecycleCoordinator lifecycle,
        IAudioDeviceEnumerator audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        ILogger<ActiveCaptureDeviceObserver> logger)
        : this(
            lifecycle,
            audioDevices,
            webcamDevices,
            logger,
            new PeriodicTimerDeviceTickSource(DefaultPollInterval),
            TimeProvider.System)
    {
    }

    internal ActiveCaptureDeviceObserver(
        CaptureLifecycleCoordinator lifecycle,
        IAudioDeviceEnumerator audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        ILogger<ActiveCaptureDeviceObserver> logger,
        IActiveCaptureDeviceTickSource ticks,
        TimeProvider timeProvider)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _audioDevices = audioDevices ?? throw new ArgumentNullException(nameof(audioDevices));
        _webcamDevices = webcamDevices ?? throw new ArgumentNullException(nameof(webcamDevices));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is not null)
                return Task.CompletedTask;

            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunAsync(_loopCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Task? loop;
        lock (_lifetimeGate)
        {
            _loopCancellation?.Cancel();
            loop = _loopTask;
        }

        // Drain the single loop, including any metadata read already in progress. Leaving this task
        // unobserved would allow a delayed old-context observation after hosted-service shutdown.
        if (loop is not null)
            await loop.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _ticks.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Per-role catalog failures are converted to correlated fail-closed observations.
            // Reaching this seam means the observer itself failed and must remain visible.
            _logger.LogError(ex, "The active capture device observer loop stopped unexpectedly");
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        // Freeze one admitted lifecycle context before any metadata provider is called.
        var snapshot = _lifecycle.Snapshot;
        var context = snapshot.Context;
        if (snapshot.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Saving) ||
            context is null)
        {
            _defaultBaselines.Clear();
            return;
        }

        RemoveOtherContextBaselines(context);
        foreach (var device in context.AdmittedDevices.Entries)
        {
            CaptureDeviceLossObservation? observation = device.Role switch
            {
                CaptureDeviceRole.DesktopAudio => ObserveAudio(
                    context,
                    device,
                    AudioEndpointDirection.Output,
                    _audioDevices.GetOutputSnapshotOrThrow),
                CaptureDeviceRole.Microphone => ObserveAudio(
                    context,
                    device,
                    AudioEndpointDirection.Input,
                    _audioDevices.GetInputSnapshotOrThrow),
                CaptureDeviceRole.Webcam => ObserveWebcam(context, device),
                _ => throw new ArgumentOutOfRangeException(nameof(device.Role)),
            };

            if (observation is null)
                continue;

            await _lifecycle.HandleDeviceLostAsync(observation, cancellationToken).ConfigureAwait(false);
            // One frozen poll cannot make useful claims about another role after it requested stop.
            break;
        }
    }

    private CaptureDeviceLossObservation? ObserveAudio(
        CaptureSessionContext context,
        AdmittedCaptureDevice device,
        AudioEndpointDirection expectedDirection,
        Func<AudioDeviceCatalogSnapshot> readSnapshot)
    {
        AudioDeviceCatalogSnapshot snapshot;
        try
        {
            snapshot = readSnapshot()
                ?? throw new InvalidDataException("The strict audio metadata snapshot was null.");
            if (snapshot.Direction != expectedDirection ||
                snapshot.WindowsDefaultRole != WindowsAudioEndpointRole.Multimedia)
            {
                throw new InvalidDataException(
                    "The strict audio metadata snapshot named the wrong direction or Windows role.");
            }
            snapshot = AudioDeviceCatalogSnapshot.CreateValidated(
                snapshot.Direction,
                snapshot.WindowsDefaultRole,
                snapshot.ActiveDevices,
                snapshot.WindowsDefaultEndpointId);
        }
        catch (Exception ex)
        {
            return CatalogFailure(context, device, ex);
        }

        if (device.SelectionMode == CaptureDeviceSelectionMode.Exact)
        {
            return snapshot.ActiveDevices.Count(candidate => StringComparer.Ordinal.Equals(
                candidate.DeviceId,
                device.RequestedDeviceId)) == 1
                ? null
                : CreateObservation(
                    context,
                    device,
                    CaptureDeviceClosedReason.ExactSelectionUnavailable);
        }

        var key = DeviceBaselineKey.From(context, device, snapshot.WindowsDefaultRole);
        var currentDefault = snapshot.WindowsDefaultEndpointId;
        if (currentDefault is null)
        {
            return CreateObservation(
                context,
                device,
                CaptureDeviceClosedReason.DefaultUnavailable,
                snapshot.WindowsDefaultRole);
        }

        if (!_defaultBaselines.TryGetValue(key, out var baseline))
        {
            _defaultBaselines.Add(key, currentDefault);
            return null;
        }

        return StringComparer.Ordinal.Equals(baseline, currentDefault)
            ? null
            : CreateObservation(
                context,
                device,
                CaptureDeviceClosedReason.DefaultChanged,
                snapshot.WindowsDefaultRole,
                baseline,
                currentDefault);
    }

    private CaptureDeviceLossObservation? ObserveWebcam(
        CaptureSessionContext context,
        AdmittedCaptureDevice device)
    {
        try
        {
            var catalog = WebcamDeviceCatalog.Validate(
                _webcamDevices.GetPhysicalDevicesOrThrow()
                ?? throw new InvalidDataException("The strict webcam metadata catalog was null."));
            return catalog.Count(candidate => StringComparer.Ordinal.Equals(
                candidate.DeviceId,
                device.RequestedDeviceId)) == 1
                ? null
                : CreateObservation(
                    context,
                    device,
                    CaptureDeviceClosedReason.ExactSelectionUnavailable);
        }
        catch (Exception ex)
        {
            return CatalogFailure(context, device, ex);
        }
    }

    private CaptureDeviceLossObservation CatalogFailure(
        CaptureSessionContext context,
        AdmittedCaptureDevice device,
        Exception exception) =>
        CreateObservation(
            context,
            device,
            exception is InvalidDataException
                ? CaptureDeviceClosedReason.CatalogInvalid
                : CaptureDeviceClosedReason.CatalogUnavailable);

    private CaptureDeviceLossObservation CreateObservation(
        CaptureSessionContext context,
        AdmittedCaptureDevice device,
        CaptureDeviceClosedReason reason,
        WindowsAudioEndpointRole? windowsDefaultRole = null,
        string? oldDefaultId = null,
        string? newDefaultId = null) =>
        new(
            context.SessionId,
            context.Request.RequestId,
            context.Generation,
            device.Role,
            device.SelectionMode,
            device.RequestedDeviceId,
            _timeProvider.GetUtcNow(),
            reason,
            windowsDefaultRole,
            oldDefaultId,
            newDefaultId);

    private void RemoveOtherContextBaselines(CaptureSessionContext context)
    {
        foreach (var key in _defaultBaselines.Keys.Where(key =>
                     key.SessionId != context.SessionId ||
                     key.RequestId != context.Request.RequestId ||
                     key.Generation != context.Generation).ToArray())
        {
            _defaultBaselines.Remove(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _loopCancellation?.Cancel();
            loop = _loopTask;
        }
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _ticks.Dispose();
        _loopCancellation?.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private readonly record struct DeviceBaselineKey(
        Guid SessionId,
        Guid RequestId,
        long Generation,
        CaptureDeviceRole Role,
        CaptureDeviceSelectionMode SelectionMode,
        string RequestedDeviceId,
        WindowsAudioEndpointRole WindowsDefaultRole)
    {
        public static DeviceBaselineKey From(
            CaptureSessionContext context,
            AdmittedCaptureDevice device,
            WindowsAudioEndpointRole defaultRole) =>
            new(
                context.SessionId,
                context.Request.RequestId,
                context.Generation,
                device.Role,
                device.SelectionMode,
                device.RequestedDeviceId,
                defaultRole);
    }
}

internal interface IActiveCaptureDeviceTickSource : IDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}

internal sealed class PeriodicTimerDeviceTickSource : IActiveCaptureDeviceTickSource
{
    private readonly PeriodicTimer _timer;

    public PeriodicTimerDeviceTickSource(TimeSpan interval)
    {
        _timer = new PeriodicTimer(interval);
    }

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        _timer.WaitForNextTickAsync(cancellationToken);

    public void Dispose() => _timer.Dispose();
}
