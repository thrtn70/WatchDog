using System.Security.Cryptography;
using System.Text;
using WatchDog.App.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.Integration.Tests;

public sealed class SafeLogRecordValidatorTests
{
    private const string CandidateName = "watchdog-safe-v2-2026-09-28-0000.log";

    [Fact]
    public void CanonicalDegradationKeepsTypedCorrelationWithoutClaimingRelease()
    {
        var operationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var line = ValidTransition(operationId, sessionId);

        Assert.True(SafeLogRecordValidator.TryParseLine(line, out var record));
        Assert.NotNull(record);
        Assert.Equal(CaptureOperationKind.HealthChanged, record.Transition?.Operation);
        Assert.Equal(CaptureCommandDisposition.Completed, record.Transition?.Disposition);
        Assert.Equal(CaptureHealth.Degraded, record.Transition?.AfterHealth);
        Assert.Equal(CaptureFailureKind.Backend, record.Transition?.AfterLastFailureKind);
        Assert.Equal(sessionId, record.Transition?.AfterSessionId);
        Assert.True(record.Transition?.AfterContextPresent);
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(CandidateName,
            SealedCandidate(line), out var records));
        Assert.Single(records);
    }

    [Theory]
    [InlineData("watchdog-2026-09-28.log")]
    [InlineData("watchdog-safe-v2-2026-09-28.log")]
    [InlineData("watchdog-safe-v2-2026-09-28-0016.log")]
    [InlineData("watchdog-safe-v2-2026-09-28-0.log")]
    [InlineData("../watchdog-safe-v2-2026-09-28-0000.log")]
    [InlineData("watchdog-safe-v2-2026-02-30-0000.log")]
    public void LegacyOrForgedFileNamesAreNotCandidates(string name)
    {
        Assert.False(SafeLogRecordValidator.IsCandidateFileName(name));
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(name,
            SealedCandidate(ValidTransition(Guid.NewGuid(), Guid.NewGuid())), out _));
    }

    [Fact]
    public void MalformedRecordsRejectTheWholeCandidateWithoutSalvage()
    {
        var line = ValidTransition(Guid.NewGuid(), Guid.NewGuid());
        var invalid = new[]
        {
            line.Replace("level=warning", "level=information", StringComparison.Ordinal),
            line.Replace("operation=HealthChanged", "operation=999", StringComparison.Ordinal),
            line.Replace("afterContextPresent=true", "afterContextPresent=false", StringComparison.Ordinal),
            line.Replace("beforeGeneration=3", "beforeGeneration=-1", StringComparison.Ordinal),
            line.Replace("afterHealth=Degraded", "afterHealth=Healthy", StringComparison.Ordinal),
            line.Replace("schema=watchdog.safe-log.v2", "schema=watchdog.safe-log.v2 secret=C:\\Private", StringComparison.Ordinal),
            line + " exception=false",
            line + "\r",
        };
        foreach (var bad in invalid)
        {
            Assert.False(SafeLogRecordValidator.TryParseLine(bad, out _));
            Assert.False(SafeLogRecordValidator.TryValidateCandidate(CandidateName,
                SealedCandidate(line, bad), out var records));
            Assert.Empty(records);
        }
    }

    [Fact]
    public void CandidateRequiresStrictUtf8LfFramingAndMatchingUtcDate()
    {
        var line = ValidTransition(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(CandidateName,
            Encoding.UTF8.GetBytes(line), out _));
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(CandidateName,
            Encoding.UTF8.GetBytes(line + "\r\n"), out _));
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(CandidateName,
            [0xFF, 0xFE, 0x0A], out _));
        Assert.False(SafeLogRecordValidator.TryValidateCandidate("watchdog-safe-v2-2026-09-29-0000.log",
            SealedCandidate(line), out _));
    }

    [Fact]
    public void ActiveTruncatedOrTamperedSegmentsRejectTheWholeCandidate()
    {
        var line = ValidTransition(Guid.NewGuid(), Guid.NewGuid());
        var sealedBytes = SealedCandidate(line);
        var text = Encoding.UTF8.GetString(sealedBytes);
        var footerStart = text.IndexOf("schema=watchdog.safe-log.close.v1", StringComparison.Ordinal);
        Assert.True(footerStart > 0);
        foreach (var bytes in new[]
        {
            Encoding.UTF8.GetBytes(line + "\n"),
            sealedBytes[..^1],
            Encoding.UTF8.GetBytes(text.Replace("records=0000000001", "records=0000000002", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace("slot=0000", "slot=0001", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace("sha256=", "sha256=0", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text + line + "\n"),
        })
        {
            Assert.False(SafeLogRecordValidator.TryValidateCandidate(CandidateName, bytes, out var records));
            Assert.Empty(records);
        }
    }

    [Fact]
    public void EventMustBelongToItsFixedSource()
    {
        const string wrong = "schema=watchdog.safe-log.v2 timeUtc=2026-09-28T18:00:00.0000000+00:00 level=error source=other exception=true event=backend-start-failed";
        Assert.False(SafeLogRecordValidator.TryParseLine(wrong, out _));
    }

    private static string ValidTransition(Guid operationId, Guid sessionId) =>
        $"schema=watchdog.safe-log.v2 timeUtc=2026-09-28T18:00:00.0000000+00:00 " +
        $"level=warning source=capture-manager exception=false event=capture-transition " +
        $"operation=HealthChanged disposition=Completed operationId={operationId:D} " +
        $"beforeSessionId={sessionId:D} afterSessionId={sessionId:D} " +
        "beforeGeneration=3 afterGeneration=3 beforeStatus=Active afterStatus=Active " +
        "beforeHealth=Healthy afterHealth=Degraded afterLastFailureKind=Backend afterContextPresent=true";

    private static byte[] SealedCandidate(params string[] lines)
    {
        var data = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        var digest = Convert.ToHexStringLower(SHA256.HashData(data));
        var footer = $"schema=watchdog.safe-log.close.v1 closed=true dateUtc=2026-09-28 slot=0000 records={lines.Length:D10} bytes={data.Length:D10} sha256={digest}\n";
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data) + footer);
    }
}
