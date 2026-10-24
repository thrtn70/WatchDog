namespace WatchDog.Core.Tests.Helpers;

/// <summary>
/// Shared bounds for test waits. Also compiled into WatchDog.Integration.Tests.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>
    /// Upper bound for a signal that must arrive: a fake reporting it was entered, a release the
    /// test always sends, or an operation that must complete. Timing out means the test failed,
    /// and passing runs return as soon as the signal fires, so a generous bound costs nothing.
    /// Bounds of one to five seconds failed on loaded Windows CI runners. Do not use this for a
    /// window that proves something does NOT happen, or for a timeout the code under test is
    /// meant to hit.
    /// </summary>
    public static readonly TimeSpan Signal = TimeSpan.FromSeconds(30);
}
