namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureRelativeOpenDisposition
{
    Absent,
    Present,
    Ambiguous,
}

/// <summary>Exact NT open-result policy, shared by the fixture and managed tests.</summary>
internal static class FixtureRelativeOpenDecision
{
    private const int StatusSuccess = 0;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private static readonly nuint FileOpened = 1;

    internal static FixtureRelativeOpenDisposition Classify(
        int status,
        nuint information,
        nint rawHandle)
    {
        var hasHandle = rawHandle is not 0 and not -1;
        if (status == StatusObjectNameNotFound && !hasHandle)
            return FixtureRelativeOpenDisposition.Absent;
        if (status == StatusSuccess && information == FileOpened && hasHandle)
            return FixtureRelativeOpenDisposition.Present;
        return FixtureRelativeOpenDisposition.Ambiguous;
    }
}
