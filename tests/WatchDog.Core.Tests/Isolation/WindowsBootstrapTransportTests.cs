using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed partial class WindowsBootstrapTransportTests
{
    private const uint HandleFlagInherit = 0x00000001;

    [Theory]
    [InlineData("descriptor-decode", ProtocolErrorCode.AuthenticationFailed, true)]
    [InlineData("descriptor-decode", ProtocolErrorCode.InvalidHeader, false)]
    [InlineData("descriptor-decode", ProtocolErrorCode.TruncatedFrame, false)]
    [InlineData("descriptor-decode", ProtocolErrorCode.JsonContractInvalid, false)]
    [InlineData("handshake", ProtocolErrorCode.AuthenticationFailed, false)]
    public void DescriptorRejectionRequiresAuthenticationFailureAtDecodeStage(
        string stage, ProtocolErrorCode code, bool expected)
    {
        Assert.Equal(expected,
            WindowsBootstrapHelperSession.ProtocolRejectionCode(stage, code) ==
            "helper.windows.bootstrap.descriptor-rejected");
    }

    [Fact]
    public async Task FreshBootstrapHandlesRemainNonInheritableOutsideAtomicLaunch()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var jobs = new WindowsJobBoundary();
        await using var transport = CreateTransport(jobs);

        Assert.Equal(32, WindowsBootstrapTransportBoundary.SecretBytes);
        Assert.Equal(2, transport.InheritedChildHandles.Count);
        AssertHandleNotInheritable(transport.ChildSectionHandle);
        AssertHandleNotInheritable(transport.ChildParentProcessHandle);
        Assert.DoesNotContain("wd-", transport.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledConnectReleasesBootstrapHandlesAndRemainsDisposeSafe()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var jobs = new WindowsJobBoundary();
        var transport = CreateTransport(jobs);
        var correlation = ProtocolFramingTests.Correlation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.AuthenticateAsync(
                new RetainedProcessIdentity(Guid.NewGuid(), correlation.HelperInstanceId),
                new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
                Limits(),
                cancellation.Token).AsTask());

        Assert.Equal(nint.Zero, transport.ChildSectionHandle);
        Assert.Equal(nint.Zero, transport.ChildParentProcessHandle);
        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public void DescriptorAndFixtureProfileKeepSensitiveSurfacesBounded()
    {
        const string endpoint = "wd-sensitive-endpoint";
        var descriptor = new BootstrapLaunchDescriptor(
            ProtocolFramingTests.Correlation(),
            AuthenticatedProtocolSession.ProtocolVersion,
            1,
            1,
            ProtocolFramingTests.Digest('c'),
            ProtocolFramingTests.Digest('d'),
            ProtocolFramingTests.Digest('e'),
            ProtocolFramingTests.BootstrapLimits(),
            endpoint,
            42,
            43);

        var rendered = descriptor.ToString();
        Assert.DoesNotContain(endpoint, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(ProtocolFramingTests.Digest('c'), rendered, StringComparison.Ordinal);
        Assert.Contains("redacted", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => new WindowsBootstrapFixtureProfile(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            2,
            ProtocolFramingTests.TestLimits()).Validate());
    }

    private static WindowsBootstrapTransportBoundary CreateTransport(WindowsJobBoundary jobs)
    {
        var correlation = ProtocolFramingTests.Correlation();
        return WindowsBootstrapTransportBoundary.Create(
            jobs,
            new WindowsBootstrapExpectation(
                new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
                1,
                1,
                ProtocolFramingTests.Digest('d'),
                ProtocolFramingTests.Digest('e'),
                Limits()),
            new WindowsBootstrapFixtureProfile(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                1,
                ProtocolFramingTests.TestLimits()));
    }

    private static CaptureIsolationLimitSnapshot Limits() => new(4096, 2048, 8, 32);

    private static void AssertHandleNotInheritable(nint handle)
    {
        Assert.NotEqual(nint.Zero, handle);
        Assert.True(GetHandleInformation(handle, out var flags));
        Assert.Equal(0u, flags & HandleFlagInherit);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);
}
