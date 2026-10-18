using System.Runtime.ExceptionServices;
using ObsKit.NET.Encoders;
using ObsKit.NET.Native.Types;
using ObsKit.NET.Outputs;
using ObsKit.NET.Sources;
using ObsKit.NET.Video;
using WatchDog.Core.Audio;
using WatchDog.Core.Devices;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Recording;

/// <summary>
/// Records the exact webcam frozen into a manual request on an independent video-only mix.
/// </summary>
public sealed class ObsWebcamRecorder : IWebcamRecorder
{
    private readonly IWebcamDeviceEnumerator _deviceEnumerator;
    private readonly IObsWebcamNativeSessionFactory _nativeSessionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IObsWebcamNativeSession? _nativeSession;
    private bool _disposed;

    public ObsWebcamRecorder(IWebcamDeviceEnumerator deviceEnumerator)
        : this(deviceEnumerator, new ObsWebcamNativeSessionFactory())
    {
    }

    internal ObsWebcamRecorder(
        IWebcamDeviceEnumerator deviceEnumerator,
        IObsWebcamNativeSessionFactory nativeSessionFactory)
    {
        _deviceEnumerator = deviceEnumerator ?? throw new ArgumentNullException(nameof(deviceEnumerator));
        _nativeSessionFactory = nativeSessionFactory ?? throw new ArgumentNullException(nameof(nativeSessionFactory));
    }

    public bool IsRecording { get; private set; }
    public bool HasActiveResources => _nativeSession?.HasActiveResources == true;
    public bool HasNativeResources => _nativeSession?.HasNativeResources == true;
    public bool HasUncertainViewRelease => _nativeSession?.HasUncertainViewRelease == true;
    public string? CurrentOutputPath { get; private set; }

    public async Task StartAsync(
        WebcamCaptureSelection selection,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var intent = ObsWebcamRecordingIntent.Create(selection, outputPath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording)
                throw new InvalidOperationException("Webcam recording is already active.");
            if (HasActiveResources)
            {
                throw new InvalidOperationException(
                    "Webcam recorder cleanup is incomplete; recording cannot start.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WatchDog.Core.Webcam.WebcamDeviceInfo> catalog;
            try
            {
                catalog = WebcamDeviceCatalog.Validate(
                    _deviceEnumerator.GetPhysicalDevicesOrThrow()
                        ?? throw new InvalidDataException("The physical webcam catalog was null."));
            }
            catch (CaptureDeviceStartupException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.Webcam,
                    CaptureDeviceStartupStage.Catalog,
                    ex is InvalidDataException
                        ? CaptureDeviceStartupReason.InvalidCatalog
                        : CaptureDeviceStartupReason.CatalogUnavailable,
                    ex is InvalidDataException
                        ? "The physical webcam catalog returned invalid device identities."
                        : "WatchDog could not read the physical webcam catalog.",
                    ex);
            }

            try
            {
                WebcamDeviceCatalog.RequireExactDevice(selection.DeviceId, catalog);
            }
            catch (Exception ex)
            {
                throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.Webcam,
                    CaptureDeviceStartupStage.Selection,
                    CaptureDeviceStartupReason.ExactSelectionUnavailable,
                    "The selected webcam is unavailable or no longer matches its saved identity.",
                    ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(intent.OutputPath)!);

            try
            {
                // Create returns a session that already owns its unconfigured source. Retain the
                // session before any source property or device-setting mutation can throw.
                try
                {
                    _nativeSession = _nativeSessionFactory.Create();
                    _nativeSession.Configure(intent);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (CaptureDeviceStartupException.Find(ex) is null)
                {
                    throw new CaptureDeviceStartupException(
                        CaptureDeviceRole.Webcam,
                        CaptureDeviceStartupStage.Binding,
                        CaptureDeviceStartupReason.Unknown,
                        "OBS could not bind the selected webcam.",
                        ex);
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool started;
                try
                {
                    started = await Task.Run(
                            () => _nativeSession.Start(cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (CaptureDeviceStartupException.Find(ex) is null)
                {
                    throw new CaptureDeviceStartupException(
                        CaptureDeviceRole.Webcam,
                        CaptureDeviceStartupStage.Start,
                        CaptureDeviceStartupReason.Unknown,
                        "OBS could not start the selected webcam.",
                        ex);
                }
                if (!started)
                {
                    var nativeFailure = new IOException(
                        string.IsNullOrWhiteSpace(_nativeSession.LastError)
                            ? "Webcam video output failed to start."
                            : $"Webcam video output failed to start: {_nativeSession.LastError}");
                    throw new CaptureDeviceStartupException(
                        CaptureDeviceRole.Webcam,
                        CaptureDeviceStartupStage.Start,
                        CaptureDeviceStartupReason.Unknown,
                        "OBS could not start the selected webcam.",
                        nativeFailure);
                }

                CurrentOutputPath = intent.OutputPath;
                IsRecording = true;
            }
            catch (Exception startFailure)
            {
                IsRecording = false;
                CurrentOutputPath = null;
                var failure = await Task.Run(() => CleanupAfterFailedStart(startFailure)).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsRecording = false;
            try
            {
                // Once stop owns the gate, native finalization is deliberately non-cancellable,
                // but it must not block the WPF caller while OBS drains the output.
                await Task.Run(CleanupNativeSession).ConfigureAwait(false);
            }
            finally
            {
                // Finalized-media validation may fail after every native object was released.
                // Retain the public path while cleanup still owns unresolved resources.
                if (!HasActiveResources)
                    CurrentOutputPath = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Exception CleanupAfterFailedStart(Exception startFailure)
    {
        try
        {
            CleanupNativeSession();
            return startFailure;
        }
        catch (Exception cleanupFailure)
        {
            return new AggregateException(
                "Webcam recording failed to start and cleanup was incomplete.",
                startFailure,
                cleanupFailure);
        }
    }

    private void CleanupNativeSession()
    {
        if (_nativeSession is null)
            return;

        var session = _nativeSession;
        try
        {
            session.Cleanup();
        }
        finally
        {
            // A failed release keeps the session and its unresolved references. Later cleanup
            // can retry supported releases or report retained uncertainty without native calls.
            if (!session.HasActiveResources)
                _nativeSession = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var disposeGate = false;
        _gate.Wait();
        try
        {
            if (_disposed)
                return;

            IsRecording = false;
            try
            {
                CleanupNativeSession();
            }
            finally
            {
                if (!HasActiveResources)
                    CurrentOutputPath = null;
            }
            _disposed = true;
            disposeGate = true;
        }
        finally
        {
            _gate.Release();
            if (disposeGate)
                _gate.Dispose();
        }
    }
}

internal sealed record ObsWebcamRecordingIntent(
    string DeviceId,
    string DisplayName,
    int Width,
    int Height,
    double FramesPerSecond,
    string VideoFormat,
    int VideoBitrateKbps,
    string OutputPath,
    string NativeOutputPath,
    RecordingFormat OutputFormat)
{
    public bool AttachMuxerCompatibilityAudioEncoder => true;
    public bool FinalMediaContainsAudio => false;

    public static ObsWebcamRecordingIntent Create(
        WebcamCaptureSelection selection,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.AuthorizationScope != WebcamCaptureAuthorizationScope.ExplicitManualRecordingOnly)
            throw new InvalidOperationException("The webcam selection is not authorized for manual recording.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("A webcam output path is required.", nameof(outputPath));

        var fullPath = Path.GetFullPath(outputPath);
        var format = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".mkv" => RecordingFormat.Mkv,
            ".mp4" => RecordingFormat.Mp4,
            _ => throw new NotSupportedException("Webcam recording output must be MKV or MP4."),
        };
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Webcam recording output must have a parent directory.", nameof(outputPath));
        var extension = Path.GetExtension(fullPath);
        var nativeOutputPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}.watchdog-mux-{Guid.NewGuid():N}{extension}");

        return new ObsWebcamRecordingIntent(
            selection.DeviceId,
            selection.DisplayName,
            selection.Width,
            selection.Height,
            selection.FramesPerSecond,
            selection.VideoFormat,
            selection.VideoBitrateKbps,
            fullPath,
            nativeOutputPath,
            format);
    }
}

internal interface IObsWebcamNativeSessionFactory
{
    IObsWebcamNativeSession Create();
}

internal interface IObsWebcamNativeSession
{
    bool HasActiveResources { get; }
    bool HasNativeResources { get; }
    bool HasUncertainViewRelease { get; }
    string? LastError { get; }

    void Configure(ObsWebcamRecordingIntent intent);
    bool Start(CancellationToken cancellationToken);
    void Cleanup();
}

internal sealed class ObsWebcamNativeSessionFactory : IObsWebcamNativeSessionFactory
{
    public IObsWebcamNativeSession Create() => new ObsWebcamNativeSession();
}

internal sealed class ObsWebcamNativeSession : IObsWebcamNativeSession
{
    private const string VideoDeviceIdSettingKey = "video_device_id";

    private WebcamCapture? _source;
    private IDisposable? _activeScope;
    private View? _view;
    private Exception? _viewReleaseFailure;
    private VideoEncoder? _videoEncoder;
    private AudioEncoder? _muxerCompatibilityAudioEncoder;
    private RecordingOutput? _output;
    private bool _outputStarted;
    private bool _outputNeedsValidation;
    private readonly FileWork _fileWork;
    private int _expectedWidth;
    private int _expectedHeight;

    public ObsWebcamNativeSession()
        : this(new FFmpegWebcamVideoOnlyFinalizer())
    {
    }

    internal ObsWebcamNativeSession(IWebcamVideoOnlyFinalizer videoOnlyFinalizer)
    {
        _fileWork = new FileWork(videoOnlyFinalizer);
        // Null is deliberate: exact SetDevice happens only after catalog validation, and the
        // wrapper is transaction-owned here before any property setter can throw.
        _source = new WebcamCapture($"WatchDog Webcam {Guid.NewGuid():N}", deviceId: null);
    }

    public bool HasNativeResources =>
        _output is not null ||
        _muxerCompatibilityAudioEncoder is not null ||
        _videoEncoder is not null ||
        _view is not null ||
        _viewReleaseFailure is not null ||
        _activeScope is not null ||
        _source is not null;
    public bool HasActiveResources => HasNativeResources || _fileWork.HasPendingWork;
    public bool HasUncertainViewRelease => _viewReleaseFailure is not null;

    public string? LastError => _output?.LastError;

    public void Configure(ObsWebcamRecordingIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var source = _source ?? throw new ObjectDisposedException(nameof(ObsWebcamNativeSession));
        if (_view is not null || _viewReleaseFailure is not null || _videoEncoder is not null || _output is not null)
            throw new InvalidOperationException("The webcam native session is already configured.");
        if (!intent.AttachMuxerCompatibilityAudioEncoder || intent.FinalMediaContainsAudio)
            throw new InvalidOperationException("The webcam muxer compatibility contract is invalid.");

        // Disable all OBS audio routing and monitoring before selecting the exact device.
        source.AudioMixers = 0;
        source.MonitoringType = ObsMonitoringType.None;
        source.SetDevice(intent.DeviceId);

        using (var settings = source.GetSettings())
        {
            var boundDeviceId = settings.GetString(VideoDeviceIdSettingKey);
            if (!StringComparer.Ordinal.Equals(boundDeviceId, intent.DeviceId))
            {
                throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.Webcam,
                    CaptureDeviceStartupStage.Binding,
                    CaptureDeviceStartupReason.IdentityMismatch,
                    "OBS did not retain the exact selected webcam identity.");
            }
        }

        source.SetCustomResolution(
            intent.Width,
            intent.Height,
            intent.FramesPerSecond,
            intent.VideoFormat);

        _activeScope = source.KeepActive();
        _expectedWidth = intent.Width;
        _expectedHeight = intent.Height;

        _view = new View();
        _view.SetSource(0, source);
        _view.AddVideoMix((uint)intent.Width, (uint)intent.Height);

        _videoEncoder = VideoEncoder.CreateX264(
            "WatchDog Webcam Video",
            intent.VideoBitrateKbps,
            "veryfast");
        _muxerCompatibilityAudioEncoder = AudioEncoder.CreateAac(
            "WatchDog Webcam Muxer Compatibility Silence",
            64,
            mixerIdx: AudioTrackPlan.WebcamMuxerCompatibilityMixerIndex);

        _output = new RecordingOutput("WatchDog Webcam Recording");
        _output.SetPath(intent.NativeOutputPath);
        _output.SetFormat(intent.OutputFormat);
        _fileWork.SetPaths(intent.NativeOutputPath, intent.OutputPath);
        _output
            .WithVideoEncoder(_videoEncoder, _view, takeOwnership: false)
            .WithAudioEncoder(_muxerCompatibilityAudioEncoder, takeOwnership: false);
    }

    public bool Start(CancellationToken cancellationToken)
    {
        var output = _output ?? throw new InvalidOperationException("The webcam native session is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        _outputStarted = output.Start();
        if (!_outputStarted)
            return false;

        // A DirectShow source may not publish its negotiated dimensions until its dedicated
        // video mix is being rendered. Retain output ownership before this bounded check so any
        // timeout or cancellation follows the same dependency-ordered failed-start cleanup.
        try
        {
            ArmFinalizedMediaValidationAfterReadiness(
                () => WaitForRequestedDimensions(
                    _source ?? throw new ObjectDisposedException(nameof(ObsWebcamNativeSession)),
                    output,
                    _expectedWidth,
                    _expectedHeight,
                    TimeSpan.FromSeconds(8),
                    cancellationToken),
                () => _outputNeedsValidation = true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (CaptureDeviceStartupException.Find(ex) is null)
        {
            throw new CaptureDeviceStartupException(
                CaptureDeviceRole.Webcam,
                CaptureDeviceStartupStage.Readiness,
                CaptureDeviceStartupReason.Unknown,
                "The selected webcam did not become ready for capture.",
                ex);
        }
        return true;
    }

    internal static void ArmFinalizedMediaValidationAfterReadiness(
        Action waitForReadiness,
        Action armFinalizedMediaValidation)
    {
        ArgumentNullException.ThrowIfNull(waitForReadiness);
        ArgumentNullException.ThrowIfNull(armFinalizedMediaValidation);
        waitForReadiness();
        armFinalizedMediaValidation();
    }

    public void Cleanup()
    {
        var failures = new List<Exception>();

        ReleaseOutput(failures);
        if (_output is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        ReleaseMuxerCompatibilityAudioEncoder(failures);
        if (_muxerCompatibilityAudioEncoder is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        ReleaseVideoEncoder(failures);
        if (_videoEncoder is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        ReleaseView(failures);
        if (_view is not null || _viewReleaseFailure is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        ReleaseActiveScope(failures);
        if (_activeScope is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        ReleaseSource(failures);
        if (_source is not null)
        {
            ThrowCleanupFailures(failures);
            return;
        }

        CompletePendingFileWork(failures);
        ThrowCleanupFailures(failures);
    }

    private void ReleaseOutput(ICollection<Exception> failures)
    {
        if (_output is null)
            return;

        var output = _output;
        if (_outputStarted || output.IsActive)
        {
            try
            {
                if (!output.Stop())
                {
                    failures.Add(new IOException("Webcam video output did not finalize before its stop timeout."));
                    return;
                }

                _outputStarted = false;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                return;
            }
        }

        var totalFrames = output.TotalFrames;
        var validateFinalizedMedia = _outputNeedsValidation;
        try
        {
            output.Dispose();
            _output = null;
            _outputStarted = false;
            _outputNeedsValidation = false;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            return;
        }

        _fileWork.ObserveOutputRelease(validateFinalizedMedia, totalFrames != 0, failures);
    }

    private void ReleaseMuxerCompatibilityAudioEncoder(ICollection<Exception> failures)
    {
        if (_muxerCompatibilityAudioEncoder is null)
            return;

        try
        {
            _muxerCompatibilityAudioEncoder.Dispose();
            _muxerCompatibilityAudioEncoder = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private void CompletePendingFileWork(ICollection<Exception> failures) => _fileWork.Complete(failures);

    // Managed file disposition only. The session remains the sole native resource owner
    // and calls Complete only after every dependent native resource has been released.
    internal sealed class FileWork(IWebcamVideoOnlyFinalizer videoOnlyFinalizer)
    {
        private readonly IWebcamVideoOnlyFinalizer _videoOnlyFinalizer =
            videoOnlyFinalizer ?? throw new ArgumentNullException(nameof(videoOnlyFinalizer));
        private bool _pendingFinalization;
        private bool _publishedPendingCleanup;
        private bool _pendingDiscard;
        private Exception? _validationFailure;
        private string? _nativeOutputPath;
        private string? _outputPath;

        public bool HasPendingWork =>
            _validationFailure is not null || _pendingFinalization || _publishedPendingCleanup ||
            _pendingDiscard || _nativeOutputPath is not null;

        public void SetPaths(string nativeOutputPath, string outputPath)
        {
            if (HasPendingWork)
                throw new InvalidOperationException("The previous webcam file work is unresolved.");
            _nativeOutputPath = nativeOutputPath;
            _outputPath = outputPath;
        }

        public void ObserveOutputRelease(
            bool validateFinalizedMedia,
            bool hasEncodedVideoFrames,
            ICollection<Exception> failures)
        {
            if (validateFinalizedMedia)
            {
                try
                {
                    ValidateFinalizedMedia(_nativeOutputPath);
                    if (!hasEncodedVideoFrames)
                        throw new IOException("Webcam output finalized without any encoded video frames.");
                    _pendingFinalization = true;
                }
                catch (Exception ex)
                {
                    // Readiness was reached: invalid output is unresolved recovery work,
                    // never the pre-readiness discard path. Keep both paths and the failure.
                    _validationFailure = ex;
                    failures.Add(ex);
                }
            }
            else
            {
                _pendingDiscard = true;
            }
        }

        public void Complete(ICollection<Exception> failures)
        {
            if (_validationFailure is not null)
            {
                // ReleaseOutput already reported this failure on the first cleanup attempt.
                // Later attempts must still fail, even if the original is now missing.
                if (!failures.Contains(_validationFailure))
                    failures.Add(_validationFailure);
                return;
            }

            var nativeOutputPath = _nativeOutputPath;
            var outputPath = _outputPath;
            if (nativeOutputPath is null)
                return;

            try
            {
                if (_pendingFinalization)
                {
                    if (outputPath is null)
                        throw new InvalidOperationException("The final webcam output path was lost before finalization.");

                    FinalizeVideoOnlyMedia(
                        _videoOnlyFinalizer,
                        nativeOutputPath,
                        outputPath,
                        () =>
                        {
                            _pendingFinalization = false;
                            _publishedPendingCleanup = true;
                        });
                    _publishedPendingCleanup = false;
                    ClearPendingPaths();
                    return;
                }

                if (_publishedPendingCleanup || _pendingDiscard)
                {
                    DeleteIntermediateMedia(nativeOutputPath);
                    _publishedPendingCleanup = false;
                    _pendingDiscard = false;
                    ClearPendingPaths();
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        private void ClearPendingPaths()
        {
            _nativeOutputPath = null;
            _outputPath = null;
        }
    }

    internal static void FinalizeVideoOnlyMedia(
        IWebcamVideoOnlyFinalizer finalizer,
        string intermediatePath,
        string finalPath,
        Action? markPublished = null)
    {
        ArgumentNullException.ThrowIfNull(finalizer);
        ValidateFinalizedMedia(intermediatePath);
        finalizer.Finalize(intermediatePath, finalPath);
        ValidateFinalizedMedia(finalPath);
        markPublished?.Invoke();
        DeleteIntermediateMedia(intermediatePath);
    }

    private static void DeleteIntermediateMedia(string intermediatePath)
    {
        if (File.Exists(intermediatePath))
            File.Delete(intermediatePath);
    }

    private static void WaitForRequestedDimensions(
        WebcamCapture source,
        RecordingOutput output,
        int expectedWidth,
        int expectedHeight,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        var observedWidth = 0;
        var observedHeight = 0;
        while (wait.Elapsed <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observedWidth = checked((int)source.Width);
            observedHeight = checked((int)source.Height);
            if (observedWidth == expectedWidth && observedHeight == expectedHeight)
            {
                return;
            }

            if (!output.IsActive)
            {
                throw new IOException(
                    string.IsNullOrWhiteSpace(output.LastError)
                        ? "The webcam video output stopped before the exact source dimensions became ready."
                        : $"The webcam video output stopped before the exact source dimensions became ready: {output.LastError}");
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"The exact webcam reported {observedWidth}x{observedHeight} instead of the requested " +
            $"{expectedWidth}x{expectedHeight} within {timeout.TotalSeconds:F0} seconds " +
            $"(output active: {output.IsActive}, output error: {output.LastError ?? "<none>"}).");
    }

    private void ReleaseVideoEncoder(ICollection<Exception> failures)
    {
        if (_videoEncoder is null)
            return;

        try
        {
            _videoEncoder.Dispose();
            _videoEncoder = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private void ReleaseView(ICollection<Exception> failures) =>
        ReleaseView(
            ref _view,
            ref _viewReleaseFailure,
            static view => view.SetSource(0, null!),
            static view => view.Dispose(),
            failures);

    internal static void ReleaseView<TView>(
        ref TView? retainedView,
        ref Exception? releaseFailure,
        Action<TView> clearSource,
        Action<TView> dispose,
        ICollection<Exception> failures)
        where TView : class
    {
        // The binding marks its wrapper disposed before fallible native cleanup. Once that
        // cleanup throws, a later no-op Dispose cannot establish release of this View.
        if (releaseFailure is not null)
        {
            if (!failures.Contains(releaseFailure))
                failures.Add(releaseFailure);
            return;
        }

        if (retainedView is null)
            return;

        var view = retainedView;
        Exception? clearFailure = null;
        try
        {
            clearSource(view);
        }
        catch (Exception ex)
        {
            clearFailure = ex;
        }

        try
        {
            dispose(view);
        }
        catch (Exception ex)
        {
            releaseFailure = clearFailure is null
                ? ex
                : new AggregateException("Webcam view cleanup is incomplete.", clearFailure, ex);
            failures.Add(releaseFailure);
            return;
        }

        retainedView = null;
        if (clearFailure is not null)
            failures.Add(clearFailure);
    }

    private void ReleaseActiveScope(ICollection<Exception> failures)
    {
        if (_activeScope is null)
            return;

        try
        {
            _activeScope.Dispose();
            _activeScope = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private void ReleaseSource(ICollection<Exception> failures)
    {
        if (_source is null)
            return;

        try
        {
            _source.Dispose();
            _source = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static void ThrowCleanupFailures(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count == 1)
            throw failures.First();
        if (failures.Count > 1)
            throw new AggregateException("Webcam native resource cleanup was incomplete.", failures);
    }

    internal static void ValidateFinalizedMedia(string? outputPath)
    {
        if (outputPath is null || new FileInfo(outputPath) is not { Exists: true, Length: > 0 })
            throw new IOException("Webcam output finalized without a non-empty media file.");
    }
}
