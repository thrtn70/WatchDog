using Microsoft.Extensions.Logging;
using WatchDog.App.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.Integration.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void DailyLogKeepsTypedCaptureCorrelationButNotUntrustedFailureDetails()
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.App.Services.CaptureSourceManager");
        var operationId = Guid.NewGuid();
        var beforeSessionId = Guid.NewGuid();
        var afterSessionId = Guid.NewGuid();
        var path = @"C:\Users\Private Person\Videos\secret.mp4";
        var title = "Private tutorial window title";
        var deviceId = "device-private-opaque-id";
        var exception = new InvalidOperationException($"{path} {title} {deviceId}");

        logger.LogWarning(exception,
            "Capture transition {Operation}: {Disposition}, {Kind}, {OperationId}, {BeforeSessionId}, {AfterSessionId}, {BeforeGeneration}, {AfterGeneration}, {BeforeStatus}, {AfterStatus}, {BeforeHealth}, {AfterHealth}, {AfterContextPresent}, {Code}, {Title}, {DeviceId}",
            CaptureOperationKind.SourceLost,
            CaptureCommandDisposition.Failed,
            CaptureFailureKind.SourceLost,
            operationId,
            beforeSessionId,
            afterSessionId,
            16L,
            17L,
            CaptureLifecycleStatus.Active,
            CaptureLifecycleStatus.Faulted,
            CaptureHealth.Healthy,
            CaptureHealth.Failed,
            true,
            path,
            title,
            deviceId);

        var line = Assert.Single(lines);
        Assert.Matches(@"timeUtc=\d{4}-\d{2}-\d{2}T", line);
        Assert.Contains("operation=SourceLost", line);
        Assert.Contains("disposition=Failed", line);
        Assert.Contains("kind=SourceLost", line);
        Assert.Contains($"operationId={operationId:D}", line);
        Assert.Contains($"beforeSessionId={beforeSessionId:D}", line);
        Assert.Contains($"afterSessionId={afterSessionId:D}", line);
        Assert.Contains("beforeGeneration=16", line);
        Assert.Contains("afterGeneration=17", line);
        Assert.Contains("beforeStatus=Active", line);
        Assert.Contains("afterStatus=Faulted", line);
        Assert.Contains("afterContextPresent=true", line);
        Assert.DoesNotContain(path, line);
        Assert.DoesNotContain(title, line);
        Assert.DoesNotContain(deviceId, line);
        Assert.DoesNotContain("Code", line);
        Assert.DoesNotContain("Title", line);
        Assert.DoesNotContain("DeviceId", line);
        Assert.DoesNotContain("InvalidOperationException", line);
    }

    [Fact]
    public void DailyLogDoesNotCallUntrustedFormatterOrSerializeExceptionAndCategory()
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.Private tutorial window title");
        var formatted = false;
        var secret = @"C:\Users\Private Person\device-private-opaque-id";

        logger.Log(LogLevel.Error, new EventId(42, secret), secret,
            new IOException(secret), (_, _) =>
            {
                formatted = true;
                return secret;
            });

        var line = Assert.Single(lines);
        Assert.False(formatted);
        Assert.Contains("exception=true", line);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain("Private tutorial window title", line);
        Assert.DoesNotContain("IOException", line);
        // Event IDs are not allowed fields; their digits may legitimately occur in timeUtc.
        Assert.True(SafeLogRecordValidator.TryParseLine(line, out var record));
        Assert.NotNull(record);
        Assert.Null(record.Event);
        Assert.Null(record.Transition);
    }

    [Fact]
    public void PrivacySchemaAcceptsEventIdDigitsInTimestampButRejectsSerializedEventId()
    {
        const string line = "schema=watchdog.safe-log.v2 timeUtc=2026-10-06T20:45:54.9342147+00:00 level=error source=other exception=true";

        Assert.True(SafeLogRecordValidator.TryParseLine(line, out var record));
        Assert.NotNull(record);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 20, 45, 54, TimeSpan.Zero).AddTicks(9342147), record.TimeUtc);
        Assert.Null(record.Event);
        Assert.Null(record.Transition);
        Assert.False(SafeLogRecordValidator.TryParseLine(line + " eventId=42", out var withEventId));
        Assert.Null(withEventId);
        Assert.False(SafeLogRecordValidator.TryParseLine(line + " event=42", out var withNumericEvent));
        Assert.Null(withNumericEvent);
    }

    [Fact]
    public void DailyLogRejectsMismatchedTypesForAllowedFieldNames()
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.App.Services.CaptureSourceManager");
        var secret = "Private tutorial window title";

        logger.LogWarning("Capture transition {Operation} {BeforeSessionId} {AfterGeneration}", secret, secret, secret);

        var line = Assert.Single(lines);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain("operation=", line);
        Assert.DoesNotContain("beforeSessionId=", line);
        Assert.DoesNotContain("afterGeneration=", line);
    }

    [Fact]
    public void IncompleteTypedTransitionFallsBackToValidMetadata()
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.App.Services.CaptureSourceManager");

        logger.LogWarning("Capture transition {Operation} {Disposition}",
            CaptureOperationKind.Stop, CaptureCommandDisposition.Failed);

        var line = Assert.Single(lines);
        Assert.DoesNotContain("event=capture-transition", line);
        Assert.Contains("source=capture-manager", line);
        Assert.True(SafeLogRecordValidator.TryParseLine(line, out _));
    }

    [Fact]
    public void SafeSinkWriteFailureDoesNotChangeCaptureResult()
    {
        using var provider = new FileLoggerProvider(_ => throw new IOException("private path"));
        var logger = provider.CreateLogger("WatchDog.App.Services.CaptureSourceManager");

        logger.LogWarning("Capture transition {Operation} {Disposition}",
            CaptureOperationKind.Stop, CaptureCommandDisposition.Failed);

        Assert.Equal(1, provider.DroppedLogCount);
    }

    [Fact]
    public void KnownCaptureErrorKeepsFixedIncidentIdentityWithoutPayload()
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        var secret = @"C:\Users\Private Person\Videos\secret.mp4";

        logger.LogError(new IOException(secret), "Failed to start desktop capture");

        var line = Assert.Single(lines);
        Assert.Contains("source=obs-capture", line);
        Assert.Contains("event=capture-start-failed", line);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain("IOException", line);
    }

    [Theory]
    [InlineData("Failed to start physical capture for request {RequestId}", "backend-start-failed")]
    [InlineData("Failed to stop {Stage} for capture session {SessionId}", "backend-stop-failed")]
    [InlineData("Failed to save replay for session {SessionId}", "backend-save-failed")]
    [InlineData("Session segment metadata remains pending for retry in {SessionId}", "segment-metadata-pending")]
    [InlineData("Session recorder error: {Error}", "session-recorder-error")]
    public void KnownBackendErrorsKeepOnlyFixedStageIdentity(string template, string expectedEvent)
    {
        var lines = new List<string>();
        using var provider = new FileLoggerProvider(lines.Add);
        var logger = provider.CreateLogger("WatchDog.App.Services.ObsCaptureLifecycleBackend");
        var secret = @"C:\Users\Private Person\device-private-opaque-id";

        logger.LogError(new IOException(secret), template, secret, secret);

        var line = Assert.Single(lines);
        Assert.Contains("source=capture-backend", line);
        Assert.Contains($"event={expectedEvent}", line);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain("IOException", line);
    }

    [Fact]
    public void InvalidLogDirectoryIsDeferredAndCannotPreventProviderConstruction()
    {
        using var provider = new FileLoggerProvider("bad\0log-directory");
        var logger = provider.CreateLogger("WatchDog.App.Services.CaptureSourceManager");

        logger.LogWarning("Capture transition {Operation} {Disposition}",
            CaptureOperationKind.Stop, CaptureCommandDisposition.Failed);

        Assert.Equal(1, provider.DroppedLogCount);
    }

    [Fact]
    public void NumberedSafeFilesRotateAtByteLimitWithoutChangingExistingFiles()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var existing = Path.Combine(root.Path, "watchdog-safe-v2-2026-09-28-0000.log");
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(existing, "synthetic pre-existing private bytes");
        using var provider = new FileLoggerProvider(root.Path, () => now, maxFileBytes: 512, maxSlotsPerDay: 3);
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");

        for (var i = 0; i < 20; i++)
            logger.LogError("Failed to start desktop capture");
        provider.Dispose();

        var files = Directory.GetFiles(root.Path, "watchdog-safe-v2-*.log");
        Assert.Equal(3, files.Length);
        Assert.Equal("synthetic pre-existing private bytes", File.ReadAllText(existing));
        Assert.All(files.Where(file => file != existing), file =>
        {
            Assert.True(new FileInfo(file).Length <= 512);
            Assert.True(SafeLogRecordValidator.TryValidateCandidate(
                Path.GetFileName(file), File.ReadAllBytes(file), out var records));
            Assert.NotEmpty(records);
        });
        Assert.True(provider.DroppedLogCount > 0);
    }

    [Fact]
    public void DisposedProviderNeverReopensOrTouchesItsSafeFile()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var provider = new FileLoggerProvider(root.Path, () => now, maxFileBytes: 512, maxSlotsPerDay: 2);
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var bytes = File.ReadAllBytes(file);
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), bytes, out _));

        logger.LogError("Failed to start desktop capture");

        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(root.Path));
        Assert.Equal(1, provider.DroppedLogCount);
    }

    [Fact]
    public void ActiveFileIsRejectedUntilCleanDisposeSealsIt()
    {
        using var root = new SyntheticLogDirectory();
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 2);
        provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine")
            .LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var snapshot = new MemoryStream();
        reader.CopyTo(snapshot);
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), snapshot.ToArray(), out var activeRecords));
        Assert.Empty(activeRecords);

        provider.Dispose();
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), File.ReadAllBytes(file), out var sealedRecords));
        Assert.Single(sealedRecords);
    }

    [Fact]
    public void PartialRecordWritePoisonsSinkAndPreservesUnsealedBytes()
    {
        using var root = new SyntheticLogDirectory();
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 2,
            createNew: path =>
            {
                creations++;
                return new SafeLogCreated(new PrefixFaultStream(new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read), 20));
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var partial = File.ReadAllBytes(file);
        Assert.Equal(20, partial.Length);
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), partial, out var records));
        Assert.Empty(records);

        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        Assert.Equal(2, provider.DroppedLogCount);
        Assert.Equal(1, creations);
        Assert.Equal(partial, File.ReadAllBytes(file));
    }

    [Fact]
    public void PartialFooterWriteOnRotationPoisonsSinkBeforeOpeningSuccessor()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, () => now, 512, 2,
            createNew: path =>
            {
                creations++;
                return new SafeLogCreated(new PrefixFaultStream(new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read), 20, faultOnWriteCall: 2));
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        now = now.AddDays(1);
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();

        var file = Assert.Single(Directory.GetFiles(root.Path));
        Assert.EndsWith("2026-09-28-0000.log", file, StringComparison.Ordinal);
        var partial = File.ReadAllBytes(file);
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), partial, out var records));
        Assert.Empty(records);
        Assert.Equal(1, creations);
        Assert.Equal(1, provider.DroppedLogCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreationFaultAfterFileAppearsIsNeverMistakenForCollision(bool collisionShapedFault)
    {
        using var root = new SyntheticLogDirectory();
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 3,
            createNew: path =>
            {
                creations++;
                using (var stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read))
                {
                    stream.Write("synthetic partial creation"u8);
                    stream.Flush();
                }
                throw collisionShapedFault
                    ? new IOException("Synthetic failure after creation", unchecked((int)0x80070050))
                    : new IOException("Synthetic failure after creation");
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var bytes = File.ReadAllBytes(file);
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();

        Assert.Equal("synthetic partial creation"u8.ToArray(), bytes);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(1, creations);
        Assert.Equal(2, provider.DroppedLogCount);
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void InvalidCreateResultPoisonsWithoutTryingAnotherSlot()
    {
        using var root = new SyntheticLogDirectory();
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 3,
            createNew: _ => { creations++; return null!; });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        Assert.Equal(1, creations);
        Assert.Equal(2, provider.DroppedLogCount);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void RecordFlushFaultPoisonsWithoutFooterOrSuccessor()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, () => now, 512, 2,
            createNew: path =>
            {
                creations++;
                return new SafeLogCreated(new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), failFlushCall: 1));
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var bytes = File.ReadAllBytes(file);
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), bytes, out _));
        now = now.AddDays(1);
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(1, creations);
        Assert.Equal(2, provider.DroppedLogCount);
    }

    [Fact]
    public void RecordFlushAndPreCloseFaultGetOneCleanupRetryThenRemainPoisoned()
    {
        using var root = new SyntheticLogDirectory();
        LifecycleFaultStream? stream = null;
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 2,
            createNew: path =>
            {
                creations++;
                stream = new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                    failFlushCall: 1, failDisposeBeforeClose: true);
                return new SafeLogCreated(stream);
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var bytes = File.ReadAllBytes(file);
        Assert.False(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), bytes, out _));
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        Assert.Equal(2, stream!.DisposeCalls);
        Assert.Equal(1, creations);
        Assert.Equal(2, provider.DroppedLogCount);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void FooterFlushFaultCanLeaveValidBytesButPoisonsBeforeSuccessor()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, () => now, 512, 2,
            createNew: path =>
            {
                creations++;
                return new SafeLogCreated(new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), failFlushCall: 2));
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        now = now.AddDays(1);
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        var bytes = File.ReadAllBytes(file);
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), bytes, out _));
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(1, creations);
        Assert.Equal(2, provider.DroppedLogCount);
    }

    [Fact]
    public void RotationDisposeFaultRetriesCleanupOnceWithoutOpeningSuccessor()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        LifecycleFaultStream? stream = null;
        var creations = 0;
        using var provider = new FileLoggerProvider(root.Path, () => now, 512, 2,
            createNew: path =>
            {
                creations++;
                stream = new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), failDisposeBeforeClose: true);
                return new SafeLogCreated(stream);
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        now = now.AddDays(1);
        logger.LogError("Failed to start desktop capture");
        var file = Assert.Single(Directory.GetFiles(root.Path));
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), File.ReadAllBytes(file), out _));
        provider.Dispose();
        Assert.Equal(2, stream!.DisposeCalls);
        Assert.Equal(1, creations);
        Assert.Equal(1, provider.DroppedLogCount);
    }

    [Fact]
    public void ShutdownDisposeFaultIsTerminalAndIdempotent()
    {
        using var root = new SyntheticLogDirectory();
        LifecycleFaultStream? stream = null;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 2,
            createNew: path =>
            {
                stream = new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), failDisposeBeforeClose: true);
                return new SafeLogCreated(stream);
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        provider.Dispose();
        logger.LogError("Failed to start desktop capture");
        Assert.Equal(2, stream!.DisposeCalls);
        Assert.Equal(2, provider.DroppedLogCount);
        var file = Assert.Single(Directory.GetFiles(root.Path));
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), File.ReadAllBytes(file), out _));
    }

    [Fact]
    public void ShutdownFooterFlushAndPreCloseFaultGetOneCleanupRetry()
    {
        using var root = new SyntheticLogDirectory();
        LifecycleFaultStream? stream = null;
        using var provider = new FileLoggerProvider(root.Path, static () =>
            new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), 512, 2,
            createNew: path =>
            {
                stream = new LifecycleFaultStream(new FileStream(path,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                    failFlushCall: 2, failDisposeBeforeClose: true);
                return new SafeLogCreated(stream);
            });
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        provider.Dispose();
        logger.LogError("Failed to start desktop capture");
        Assert.Equal(2, stream!.DisposeCalls);
        Assert.Equal(2, provider.DroppedLogCount);
        var file = Assert.Single(Directory.GetFiles(root.Path));
        Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), File.ReadAllBytes(file), out _));
    }

    [Fact]
    public void UtcRolloverAndRollbackSealEachOriginalSegment()
    {
        using var root = new SyntheticLogDirectory();
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        using var provider = new FileLoggerProvider(root.Path, () => now, 512, 2);
        var logger = provider.CreateLogger("WatchDog.Core.Capture.ObsCaptureEngine");
        logger.LogError("Failed to start desktop capture");
        now = now.AddDays(1);
        logger.LogError("Failed to start desktop capture");
        now = now.AddDays(-1);
        logger.LogError("Failed to start desktop capture");
        provider.Dispose();
        var files = Directory.GetFiles(root.Path);
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.True(SafeLogRecordValidator.TryValidateCandidate(
            Path.GetFileName(file), File.ReadAllBytes(file), out _)));
    }

    private sealed class PrefixFaultStream(Stream inner, int prefixBytes, int faultOnWriteCall = 1) : Stream
    {
        private int _writeCalls;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _writeCalls++;
            if (_writeCalls < faultOnWriteCall)
            {
                inner.Write(buffer);
                return;
            }
            if (_writeCalls > faultOnWriteCall) throw new IOException("Synthetic write fault.");
            inner.Write(buffer[..Math.Min(prefixBytes, buffer.Length)]);
            inner.Flush();
            throw new IOException("Synthetic write fault.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class LifecycleFaultStream(Stream inner, int failFlushCall = 0,
        bool failDisposeBeforeClose = false) : Stream
    {
        private int _flushCalls;
        public int DisposeCalls { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush()
        {
            _flushCalls++;
            inner.Flush();
            if (_flushCalls == failFlushCall) throw new IOException("Synthetic flush fault.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                if (failDisposeBeforeClose && DisposeCalls == 1)
                    throw new IOException("Synthetic disposal fault before close.");
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class SyntheticLogDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"watchdog-safe-log-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(full).StartsWith("watchdog-safe-log-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected synthetic log test directory.");
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
    }
}
