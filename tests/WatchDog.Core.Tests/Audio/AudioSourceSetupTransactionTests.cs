using WatchDog.Core.Capture;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Tests.Audio;

public sealed class AudioSourceSetupTransactionTests
{
    [Fact]
    public void BothExplicitSelectionsResolveBeforeEitherNativeFactoryRuns()
    {
        var factoryCalls = 0;

        var error = Assert.Throws<CaptureDeviceStartupException>(() =>
            AudioSourceSetupTransaction.ResolveAndCreate(
                desktopEnabled: true,
                resolveDesktop: () => "speaker",
                createDesktop: _ =>
                {
                    factoryCalls++;
                    return new DisposableSource();
                },
                microphoneEnabled: true,
                resolveMicrophone: () => throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.Microphone,
                    CaptureDeviceStartupStage.Selection,
                    CaptureDeviceStartupReason.ExactSelectionUnavailable,
                    "The selected microphone is unavailable."),
                createMicrophone: _ =>
                {
                    factoryCalls++;
                    return new DisposableSource();
                },
                bind: (_, _) => { },
                unbind: () => { },
                retainForRetry: (_, _) => { }));

        Assert.Equal(CaptureDeviceRole.Microphone, error.Role);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void FrozenManifestUsesResolvedFactoryIdsAndOmitsDisabledRoles()
    {
        var mutableConfiguredId = "speaker-a";
        var manifest = ObsCaptureEngine.CreateAdmittedAudioManifest(
            mutableConfiguredId,
            resolvedMicrophoneDeviceId: null);

        mutableConfiguredId = "speaker-b";

        var admitted = Assert.Single(manifest.Entries);
        Assert.Equal(CaptureDeviceRole.DesktopAudio, admitted.Role);
        Assert.Equal("speaker-a", admitted.RequestedDeviceId);
        Assert.DoesNotContain(manifest.Entries, entry => entry.Role == CaptureDeviceRole.Microphone);
    }

    [Fact]
    public void MicrophoneCreationFailureDisposesPartialDesktopSourceAndUnbinds()
    {
        var desktop = new DisposableSource();
        var unbinds = 0;
        var binds = 0;

        Assert.Throws<InvalidOperationException>(() =>
            AudioSourceSetupTransaction.Create<DisposableSource, DisposableSource>(
                desktopEnabled: true,
                createDesktop: () => desktop,
                microphoneEnabled: true,
                createMicrophone: () => throw new InvalidOperationException("missing microphone"),
                bind: (_, _) => binds++,
                unbind: () => unbinds++,
                retainForRetry: (_, _) => { }));

        Assert.True(desktop.Disposed);
        Assert.Equal(0, binds);
        Assert.Equal(1, unbinds);
    }

    [Fact]
    public void BindFailureDisposesBothCreatedSourcesAndUnbinds()
    {
        var desktop = new DisposableSource();
        var microphone = new DisposableSource();
        var unbinds = 0;

        Assert.Throws<InvalidOperationException>(() =>
            AudioSourceSetupTransaction.Create(
                desktopEnabled: true,
                createDesktop: () => desktop,
                microphoneEnabled: true,
                createMicrophone: () => microphone,
                bind: (_, _) => throw new InvalidOperationException("OBS slot bind failed"),
                unbind: () => unbinds++,
                retainForRetry: (_, _) => { }));

        Assert.True(desktop.Disposed);
        Assert.True(microphone.Disposed);
        Assert.Equal(1, unbinds);
    }

    [Fact]
    public void SuccessfulSetupTransfersOwnershipWithoutDisposal()
    {
        var desktop = new DisposableSource();
        var microphone = new DisposableSource();

        var created = AudioSourceSetupTransaction.Create(
            desktopEnabled: true,
            createDesktop: () => desktop,
            microphoneEnabled: true,
            createMicrophone: () => microphone,
            bind: (_, _) => { },
            unbind: () => throw new InvalidOperationException("must not roll back"),
            retainForRetry: (_, _) => throw new InvalidOperationException("must not retain"));

        Assert.Same(desktop, created.Desktop);
        Assert.Same(microphone, created.Microphone);
        Assert.False(desktop.Disposed);
        Assert.False(microphone.Disposed);
    }

    [Fact]
    public void RollbackDisposeFailureRetainsResidualSourceForRetry()
    {
        var desktop = new DisposableSource { ThrowOnDispose = true };
        DisposableSource? retainedDesktop = null;
        DisposableSource? retainedMicrophone = null;

        var failure = Assert.Throws<AggregateException>(() =>
            AudioSourceSetupTransaction.Create<DisposableSource, DisposableSource>(
                desktopEnabled: true,
                createDesktop: () => desktop,
                microphoneEnabled: false,
                createMicrophone: () => throw new InvalidOperationException("must not create"),
                bind: (_, _) => throw new InvalidOperationException("OBS slot bind failed"),
                unbind: () => { },
                retainForRetry: (residualDesktop, residualMicrophone) =>
                {
                    retainedDesktop = residualDesktop;
                    retainedMicrophone = residualMicrophone;
                }));

        Assert.Contains(failure.InnerExceptions, error => error.Message.Contains("dispose", StringComparison.OrdinalIgnoreCase));
        Assert.Same(desktop, retainedDesktop);
        Assert.Null(retainedMicrophone);
        Assert.False(desktop.Disposed);
        Assert.Equal(1, desktop.DisposeAttempts);

        desktop.ThrowOnDispose = false;
        retainedDesktop!.Dispose();

        Assert.True(desktop.Disposed);
        Assert.Equal(2, desktop.DisposeAttempts);
    }

    private sealed class DisposableSource : IDisposable
    {
        public bool ThrowOnDispose { get; set; }
        public int DisposeAttempts { get; private set; }
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            DisposeAttempts++;
            if (ThrowOnDispose)
                throw new IOException("Synthetic dispose failure");
            Disposed = true;
        }
    }
}
