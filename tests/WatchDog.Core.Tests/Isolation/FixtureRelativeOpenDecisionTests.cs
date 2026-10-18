using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureRelativeOpenDecisionTests
{
    private const int Absent = (int)FixtureRelativeOpenDisposition.Absent;
    private const int Present = (int)FixtureRelativeOpenDisposition.Present;
    private const int Ambiguous = (int)FixtureRelativeOpenDisposition.Ambiguous;

    [Theory]
    [InlineData(unchecked((int)0xC0000034), 0UL, 0L, Absent)]
    [InlineData(unchecked((int)0xC0000034), 9UL, -1L, Absent)]
    [InlineData(unchecked((int)0xC0000034), 0UL, 1234L, Ambiguous)]
    [InlineData(0, 1UL, 1234L, Present)]
    [InlineData(0, 0UL, 1234L, Ambiguous)]
    [InlineData(0, 1UL, 0L, Ambiguous)]
    [InlineData(0, 1UL, -1L, Ambiguous)]
    [InlineData(unchecked((int)0xC000003A), 0UL, 0L, Ambiguous)]
    [InlineData(unchecked((int)0xC0000043), 0UL, 0L, Ambiguous)]
    [InlineData(0x00000104, 1UL, 1234L, Ambiguous)]
    [InlineData(unchecked((int)0xC0000022), 1UL, 1234L, Ambiguous)]
    public void OnlyExactStatusInformationAndHandleCombinationsAreAccepted(
        int status,
        ulong information,
        long rawHandle,
        int expected)
    {
        Assert.Equal((FixtureRelativeOpenDisposition)expected, FixtureRelativeOpenDecision.Classify(
            status, checked((nuint)information), checked((nint)rawHandle)));
    }
}
