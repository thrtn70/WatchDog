using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WatchDog.CaptureIsolation.Windows.Qualification;
using Xunit.Abstractions;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1NativeJournalAdversarialNativeTests(
    ITestOutputHelper output)
{
    private const string OptInVariable = "WATCHDOG_RUN_NATIVE_B1_FIXTURE";
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void OptInHardLinkAttemptIsDeniedOrRejectedFromRetainedHandles()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "hardlink");
        var link = Path.Combine(fixture.RootPath, "frames-link.bin");
        fixture.KnownFiles.Add(link);
        var before = fixture.CaptureFramesEvidence("hard-link before");

        var changed = AttemptMutation("hard-link", () =>
        {
            if (!CreateHardLink(link, fixture.FramesPath, nint.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            fixture.DeferPathCleanup();
        });

        AssertMutationResult(fixture, "hard-link", changed, before);
    }

    [Fact]
    public void OptInRenameAttemptIsDeniedOrRejectedFromRetainedHandles()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "rename");
        var renamed = Path.Combine(fixture.RootPath, "frames-renamed.bin");
        fixture.KnownFiles.Add(renamed);
        var before = fixture.CaptureFramesEvidence("rename before");

        var changed = AttemptMutation("rename", () =>
        {
            File.Move(fixture.FramesPath, renamed);
            fixture.DeferPathCleanup();
        });

        AssertMutationResult(fixture, "rename", changed, before);
    }

    [Fact]
    public void OptInAncestorRenameAndSymlinkAttemptIsDeniedOrRejectedFromRetainedHandles()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "ancestor-link");
        var original = fixture.ScratchPath;
        var moved = original + "-moved";
        var renamed = false;
        var linked = false;
        Exception? denial = null;
        var before = fixture.CaptureFramesEvidence("ancestor-reparse before");
        try
        {
            Directory.Move(original, moved);
            renamed = true;
            fixture.DeferPathCleanup();
            fixture.MoveScratchTo(moved);
            Directory.CreateSymbolicLink(original, moved);
            linked = true;
            fixture.SymbolicLinkPath = original;
        }
        catch (Exception exception) when (IsExpectedMutationDenial(exception))
        {
            denial = exception;
        }

        if (renamed)
        {
            _output.WriteLine(linked
                ? "ancestor-reparse: rename and symbolic-link substitution succeeded; checking retained handles"
                : $"ancestor-reparse: ancestor rename succeeded; symbolic-link substitution was denied by OS ({Describe(denial!)})");
            AssertMutationResult(fixture, "ancestor-reparse", changed: true, before: before);
        }
        else
        {
            _output.WriteLine(
                $"ancestor-reparse: OS denied ancestor rename ({Describe(denial!)}); no drift-detection claim");
            AssertMutationResult(fixture, "ancestor-reparse", changed: false, before: before);
        }
    }

    [Fact]
    public void OptInAclMutationAttemptIsDeniedOrRejectedFromRetainedHandles()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "acl");
        var before = fixture.CaptureFramesEvidence("ACL before");

        var changed = AttemptMutation("ACL", () =>
        {
            var current = WindowsIdentity.GetCurrent().User!;
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var security = new FileSecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(current,
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(world,
                FileSystemRights.ReadData, AccessControlType.Allow));
            new FileInfo(fixture.FramesPath).SetAccessControl(security);
        });

        AssertMutationResult(fixture, "ACL", changed, before);
    }

    [Fact]
    public void OptInAlternateDataStreamAttemptIsDeniedOrRejectedFromRetainedHandles()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "ads");
        var streamPath = fixture.FramesPath + ":watchdog_probe";
        fixture.KnownFiles.Add(streamPath);
        var before = fixture.CaptureFramesEvidence("ADS before");

        var changed = AttemptMutation("ADS", () =>
        {
            using var stream = new FileStream(streamPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read);
            stream.WriteByte(0x5A);
            stream.Flush(flushToDisk: true);
        });

        AssertMutationResult(fixture, "ADS", changed, before);
    }

    [Fact]
    public void OptInPreAcquisitionReparseAncestorFailsBeforePrivateRootCreation()
    {
        if (!NativeOptIn()) return;
        var scratch = Path.Combine(Path.GetTempPath(),
            "wd-b1-adversarial-prelink-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(scratch, "target");
        var alias = Path.Combine(scratch, "alias");
        var rootName = "journal-" + Guid.NewGuid().ToString("N");
        var targetJournal = Path.Combine(target, rootName);
        var controlName = "control-" + Guid.NewGuid().ToString("N");
        var controlRoot = Path.Combine(target, controlName);
        FixtureB1NativeJournalAcquisition? acquisition = null;
        FixtureB1NativeJournalAcquisition? control = null;
        var preservePaths = false;
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(target);
        try
        {
            control = FixtureB1NativeJournalObservation.Acquire(
                FixtureB1NativeJournalObservationNative.Instance,
                new TestWriterAuthority(),
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
                CreateOptions(target, controlName));
            Assert.True(control.Succeeded,
                $"nonreparse control failed: {control.Failure?.Code}");
            Assert.True(control.Lease!.Close().Complete);
            Assert.True(control.CleanupOwner.CloseAll().Complete);
            DeleteKnownFile(Path.Combine(controlRoot, "frames.bin"));
            DeleteKnownFile(Path.Combine(controlRoot, "anchor.bin"));
            DeleteKnownDirectory(controlRoot);
            control = null;
            _output.WriteLine("nonreparse control: same target parent admitted and closed");

            var linkKind = "symbolic link";
            try
            {
                Directory.CreateSymbolicLink(alias, target);
            }
            catch (Exception exception) when (IsExpectedMutationDenial(exception))
            {
                if (!TryCreateJunction(alias, target, out var junctionFailure))
                {
                    _output.WriteLine(
                        $"pre-acquisition reparse: symlink denied ({Describe(exception)}); junction denied ({junctionFailure}); no detector claim");
                    return;
                }
                linkKind = "junction";
            }

            acquisition = FixtureB1NativeJournalObservation.Acquire(
                FixtureB1NativeJournalObservationNative.Instance,
                new TestWriterAuthority(),
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
                CreateOptions(alias, rootName));
            Assert.False(acquisition.Succeeded);
            Assert.Null(acquisition.Lease);
            Assert.Equal("native-journal.directory-rejected", acquisition.Failure?.Code);
            Assert.False(Directory.Exists(targetJournal));
            _output.WriteLine(
                $"pre-acquisition ancestor {linkKind}: handle-derived acquisition rejected with {acquisition.Failure?.Code}; no private root created");
        }
        finally
        {
            if (control is not null)
            {
                var controlClose = control.CleanupOwner.CloseAll();
                Assert.True(controlClose.Complete);
                preservePaths = true;
                _output.WriteLine($"incomplete nonreparse control at {controlRoot}; path cleanup deferred");
            }
            if (acquisition is not null)
            {
                var close = acquisition.CleanupOwner.CloseAll();
                _output.WriteLine(
                    $"pre-acquisition cleanup: complete={close.Complete} uncertain={close.UncertainHandleIds.Count}");
                Assert.True(close.Complete);
            }
            if (preservePaths || Directory.Exists(targetJournal))
            {
                _output.WriteLine($"unexpected private root at {targetJournal}; path cleanup deferred");
            }
            else
            {
                if (Directory.Exists(alias)) Directory.Delete(alias);
                DeleteKnownDirectory(target);
                DeleteKnownDirectory(scratch);
            }
        }
    }

    [Fact]
    public void OptInFileCreateCollisionFailedOpenAndExplicitCloseAreBounded()
    {
        if (!NativeOptIn()) return;
        using var fixture = DirectNativeFixture.Create(_output, "create-collision");
        var first = fixture.Require(fixture.Api.CreateFileRelative(
            fixture.PrivateRoot, "collision.bin", fixture.Descriptor), "initial FILE_CREATE");
        fixture.KnownFiles.Add(Path.Combine(fixture.PrivatePath, "collision.bin"));
        var before = fixture.RequireObservation(first, "FILE_CREATE before collision");

        var collision = fixture.Api.CreateFileRelative(
            fixture.PrivateRoot, "collision.bin", fixture.Descriptor);
        fixture.AdoptReturnedHandle(collision);
        Assert.False(collision.Succeeded);
        Assert.Equal("native-journal.already-exists", collision.FailureCode);
        _output.WriteLine($"FILE_CREATE collision: rejected with {collision.FailureCode}");

        var missing = fixture.Api.OpenDirectoryRelative(fixture.PrivateRoot, "missing");
        fixture.AdoptReturnedHandle(missing);
        Assert.False(missing.Succeeded);
        Assert.NotNull(missing.FailureCode);
        _output.WriteLine($"failed relative open: rejected with {missing.FailureCode}");

        var after = fixture.RequireObservation(first, "FILE_CREATE after failed operations");
        Assert.True(FixtureB1NativeJournalObservation.SameObject(before, after));
        LogEvidence(_output, "FILE_CREATE before", before);
        LogEvidence(_output, "FILE_CREATE after", after);

        var close = fixture.CloseHandle(first);
        Assert.Equal(FixtureB1NativeJournalCloseDisposition.Closed, close.Disposition);
        _output.WriteLine("explicit close: original file handle closed exactly once");
    }

    [Fact]
    public void OptInNonemptyPostWriterReadReturnsQualificationOnlySampleBytes()
    {
        if (!NativeOptIn()) return;
        using var fixture = NativeLeaseFixture.Create(_output, "post-close-read");
        var payload = Encoding.UTF8.GetBytes("watchdog-b1-native-sample");
        var before = fixture.CaptureFramesEvidence("sample write before");
        var writer = typeof(FixtureB1NativeJournalObservationLease).GetField(
            "_framesWriter", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(writer);
        var writerHandle = writer!.GetValue(fixture.Lease);
        Assert.NotNull(writerHandle);
        var rawProperty = writerHandle.GetType().GetProperty(
            "Raw", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(rawProperty);
        var rawValue = rawProperty!.GetValue(writerHandle);
        var raw = (nint)Assert.IsType<IntPtr>(rawValue);

        Assert.True(WriteFile(raw, payload, checked((uint)payload.Length),
            out var written, nint.Zero),
            $"qualification-only WriteFile failed: {Marshal.GetLastPInvokeError()}");
        Assert.Equal((uint)payload.Length, written);
        Assert.True(FlushFileBuffers(raw),
            $"qualification-only FlushFileBuffers failed: {Marshal.GetLastPInvokeError()}");
        var afterWrite = fixture.CaptureFramesEvidence("sample write before writer close");
        AssertIdentityContinuity(before, afterWrite, "sample write");
        var writerClose = fixture.Lease.CloseWriterCustody();
        _output.WriteLine(
            $"sample writer close: complete={writerClose.Complete} uncertain={writerClose.UncertainHandleIds.Count}");
        Assert.True(writerClose.Complete);

        var observation = fixture.Lease.ObserveRetainedStorageForRead();
        Assert.True(observation.Admitted, observation.Failure?.Code);
        Assert.Equal(payload, observation.FramesBytes);
        Assert.Empty(observation.AnchorBytes!);
        Assert.False(observation.NativeAuthorityProven);
        Assert.False(observation.NativeDurabilityProven);
        AssertIdentityContinuity(before, observation.Frames, "post-writer read");
        LogEvidence(_output, "post-writer read", observation.Frames!);
        _output.WriteLine(
            $"post-writer read: returned {payload.Length} qualification-only sample bytes; no B1 effect or durability claim");
    }

    private bool NativeOptIn()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) == "1")
        {
            Assert.True(OperatingSystem.IsWindows());
            return true;
        }
        _output.WriteLine($"native fixture not run; set {OptInVariable}=1 to opt in");
        return false;
    }

    private bool AttemptMutation(string label, Action mutation)
    {
        try
        {
            mutation();
            _output.WriteLine($"{label}: mutation succeeded; checking retained-handle rejection");
            return true;
        }
        catch (Exception exception) when (IsExpectedMutationDenial(exception))
        {
            _output.WriteLine(
                $"{label}: OS denied mutation ({Describe(exception)}); no drift-detection claim");
            return false;
        }
    }

    private void AssertMutationResult(
        NativeLeaseFixture fixture,
        string label,
        bool changed,
        FixtureB1NativeJournalHandleObservation? before)
    {
        if (changed && label is ("hard-link" or "rename" or "ancestor-reparse"))
            fixture.DeferPathCleanup();
        var after = fixture.CaptureFramesEvidence(label + " after", requireSuccess: false);
        AssertIdentityContinuity(before, after, label);
        var admission = fixture.Lease.AuthorizeLiveWriter();
        if (changed)
        {
            _output.WriteLine(
                $"{label}: handle-derived admission rejected mutation with {admission.Failure?.Code}");
            Assert.False(admission.Admitted);
            Assert.NotNull(admission.Failure);
            var expectedCodes = label switch
            {
                "hard-link" or "ADS" => new[] { "native-journal.file-rejected" },
                "ACL" => ["native-journal.security-rejected"],
                "rename" or "ancestor-reparse" =>
                    ["native-journal.final-path-mismatch", "native-journal.object-drift",
                        "native-journal.root-entries-unexpected"],
                _ => throw new InvalidOperationException("Unknown mutation type."),
            };
            Assert.Contains(admission.Failure.Code, expectedCodes);
        }
        else
        {
            _output.WriteLine(
                $"{label}: baseline remained admitted after denied attempt; no mutation was observed");
            Assert.True(admission.Admitted, admission.Failure?.Code);
        }
        _output.WriteLine(
            $"{label}: byte read deferred because writer originals remain open; no byte-content claim");
    }

    private static void LogEvidence(
        ITestOutputHelper output,
        string stage,
        FixtureB1NativeJournalHandleObservation observation) =>
        output.WriteLine(
            $"{stage}: id={observation.VolumeSerialNumber:X16}:{observation.FileIdHigh:X16}{observation.FileIdLow:X16} " +
            $"access=0x{observation.GrantedAccessMask:X8} links={observation.NumberOfLinks} " +
            $"streams=[{string.Join(',', observation.StreamNames)}] eof={observation.EndOfFile}");

    private static void AssertIdentityContinuity(
        FixtureB1NativeJournalHandleObservation? before,
        FixtureB1NativeJournalHandleObservation? after,
        string label)
    {
        if (before is null || after is null) return;
        Assert.True(FixtureB1NativeJournalObservation.SameObject(before, after),
            $"{label}: retained file identity changed");
    }

    private static bool IsExpectedMutationDenial(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or
            PlatformNotSupportedException or NotSupportedException;

    private static string Describe(Exception exception) => exception switch
    {
        Win32Exception win32 => $"{exception.GetType().Name} {win32.NativeErrorCode}: {exception.Message}",
        _ => $"{exception.GetType().Name} 0x{exception.HResult:X8}: {exception.Message}",
    };

    private static bool TryCreateJunction(
        string alias, string target, out string failure)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " +
            Literal(alias) + " -Target " + Literal(target) + " | Out-Null";
        var start = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                failure = "PowerShell did not start";
                return false;
            }
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                failure = "junction creation timed out";
                return false;
            }
            failure = process.StandardError.ReadToEnd().Trim();
            return process.ExitCode == 0 && Directory.Exists(alias);
        }
        catch (Exception exception) when (IsExpectedMutationDenial(exception))
        {
            failure = Describe(exception);
            return false;
        }
    }

    private static byte[] Descriptor(SecurityIdentifier owner)
    {
        var descriptor = new RawSecurityDescriptor(
            $"O:{owner.Value}G:{owner.Value}D:P(A;;FA;;;SY)(A;;FA;;;{owner.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static FixtureB1NativeJournalSecurityPolicy SecurityPolicy(
        SecurityIdentifier owner,
        byte[] descriptor)
    {
        var raw = new RawSecurityDescriptor(descriptor, 0);
        var aces = raw.DiscretionaryAcl!.Cast<CommonAce>()
            .Select(ace => new FixtureB1NativeJournalAceObservation(
                ace.AceQualifier == AceQualifier.AccessAllowed ?
                    FixtureB1NativeJournalAceKind.Allow :
                    FixtureB1NativeJournalAceKind.Deny,
                ace.SecurityIdentifier.Value,
                unchecked((uint)ace.AccessMask),
                (byte)ace.AceFlags)).ToArray();
        return new(descriptor, owner.Value, true, aces);
    }

    private static FixtureB1NativeJournalAcquisitionOptions CreateOptions(
        string parentPath,
        string rootName)
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var descriptor = Descriptor(sid);
        var security = SecurityPolicy(sid, descriptor);
        var driveRoot = Path.GetPathRoot(parentPath)!;
        var components = parentPath[driveRoot.Length..].Split('\\',
            StringSplitOptions.RemoveEmptyEntries);
        var access = new FixtureB1NativeJournalAccessPolicy(1, 2, 1,
            (uint)(FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile |
                FixtureB1NativeJournalHandleAccess.AppendDataOrAddSubdirectory |
                FixtureB1NativeJournalHandleAccess.Delete |
                FixtureB1NativeJournalHandleAccess.WriteDac |
                FixtureB1NativeJournalHandleAccess.WriteOwner));
        return new(driveRoot, components, rootName, "frames.bin", "anchor.bin",
            4 * 1024, 4 * 1024, security, security, access);
    }

    private sealed class NativeLeaseFixture : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private bool _disposed;
        private bool _namespaceChanged;

        private NativeLeaseFixture(
            ITestOutputHelper output,
            string scratchPath,
            string rootName,
            FixtureB1NativeJournalAcquisition acquisition)
        {
            _output = output;
            ScratchPath = scratchPath;
            CurrentScratchPath = scratchPath;
            RootName = rootName;
            Acquisition = acquisition;
            Lease = acquisition.Lease!;
        }

        internal string ScratchPath { get; }
        internal string CurrentScratchPath { get; private set; }
        internal string RootName { get; }
        internal string? SymbolicLinkPath { get; set; }
        internal FixtureB1NativeJournalAcquisition Acquisition { get; }
        internal FixtureB1NativeJournalObservationLease Lease { get; }
        internal HashSet<string> KnownFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal string RootPath => Path.Combine(CurrentScratchPath, RootName);
        internal string FramesPath => Path.Combine(RootPath, "frames.bin");
        internal string AnchorPath => Path.Combine(RootPath, "anchor.bin");

        internal static NativeLeaseFixture Create(ITestOutputHelper output, string label)
        {
            var scratch = Path.Combine(Path.GetTempPath(),
                $"wd-b1-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            var rootName = "journal-" + Guid.NewGuid().ToString("N");
            FixtureB1NativeJournalAcquisition? acquisition = null;
            try
            {
                acquisition = FixtureB1NativeJournalObservation.Acquire(
                    FixtureB1NativeJournalObservationNative.Instance,
                    new TestWriterAuthority(),
                    new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
                    CreateOptions(scratch, rootName));
                Assert.True(acquisition.Succeeded,
                    $"native acquisition failed: {acquisition.Failure?.Code} at {scratch}");
                return new(output, scratch, rootName, acquisition);
            }
            catch
            {
                var close = acquisition?.CleanupOwner.CloseAll();
                output.WriteLine($"acquisition failed at {scratch}; cleanup complete={close?.Complete} uncertain={close?.UncertainHandleIds.Count}; path cleanup deferred");
                throw;
            }
        }

        internal void MoveScratchTo(string path) => CurrentScratchPath = path;
        internal void DeferPathCleanup() => _namespaceChanged = true;

        internal FixtureB1NativeJournalHandleObservation? CaptureFramesEvidence(
            string stage,
            bool requireSuccess = true)
        {
            var handle = GetLeaseHandle(Lease, "_framesWriter");
            var result = FixtureB1NativeJournalObservationNative.Instance.Observe(handle);
            if (!result.Succeeded)
            {
                _output.WriteLine($"{stage}: observation unavailable ({result.FailureCode})");
                if (requireSuccess)
                    Assert.True(result.Succeeded, result.FailureCode);
                return null;
            }
            LogEvidence(_output, stage, result.Observation!);
            return result.Observation;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var close = Lease.Close();
            _output.WriteLine(
                $"lease close: complete={close.Complete} uncertain={close.UncertainHandleIds.Count}");
            Assert.True(close.Complete,
                "native fixture close left uncertain handles: " +
                string.Join(",", close.UncertainHandleIds));
            Assert.True(Acquisition.CleanupOwner.CloseAll().Complete);

            if (_namespaceChanged)
            {
                _output.WriteLine($"namespace changed; path cleanup deferred at {CurrentScratchPath}");
                return;
            }

            if (SymbolicLinkPath is not null && Directory.Exists(SymbolicLinkPath))
                Directory.Delete(SymbolicLinkPath);
            foreach (var file in KnownFiles)
                DeleteKnownFile(Rebase(file));
            DeleteKnownFile(FramesPath);
            DeleteKnownFile(AnchorPath);
            DeleteKnownDirectory(RootPath);
            DeleteKnownDirectory(CurrentScratchPath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(CurrentScratchPath, ScratchPath))
                DeleteKnownDirectory(ScratchPath);
            _output.WriteLine("cleanup: all retained handles explicitly closed; known paths removed without recursion");
        }

        private string Rebase(string path)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(CurrentScratchPath, ScratchPath) ||
                !path.StartsWith(ScratchPath, StringComparison.OrdinalIgnoreCase))
                return path;
            return CurrentScratchPath + path[ScratchPath.Length..];
        }

        private static IFixtureB1NativeJournalHandle GetLeaseHandle(
            FixtureB1NativeJournalObservationLease lease,
            string fieldName)
        {
            var field = typeof(FixtureB1NativeJournalObservationLease).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsAssignableFrom<IFixtureB1NativeJournalHandle>(field!.GetValue(lease));
        }
    }

    private sealed class DirectNativeFixture : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly List<IFixtureB1NativeJournalHandle> _handles = [];
        private readonly HashSet<Guid> _closed = [];
        private bool _disposed;

        private DirectNativeFixture(
            ITestOutputHelper output,
            string scratchPath,
            string privateName,
            byte[] descriptor,
            IFixtureB1NativeJournalHandle privateRoot)
        {
            _output = output;
            ScratchPath = scratchPath;
            PrivateName = privateName;
            Descriptor = descriptor;
            PrivateRoot = privateRoot;
        }

        internal FixtureB1NativeJournalObservationNative Api =>
            FixtureB1NativeJournalObservationNative.Instance;
        internal string ScratchPath { get; }
        internal string PrivateName { get; }
        internal string PrivatePath => Path.Combine(ScratchPath, PrivateName);
        internal byte[] Descriptor { get; }
        internal IFixtureB1NativeJournalHandle PrivateRoot { get; }
        internal HashSet<string> KnownFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal static DirectNativeFixture Create(ITestOutputHelper output, string label)
        {
            var scratch = Path.Combine(Path.GetTempPath(),
                $"wd-b1-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            var privateName = "journal-" + Guid.NewGuid().ToString("N");
            var descriptor = Descriptor(WindowsIdentity.GetCurrent().User!);
            DirectNativeFixture? fixture = null;
            var api = FixtureB1NativeJournalObservationNative.Instance;
            var handles = new List<IFixtureB1NativeJournalHandle>();
            try
            {
                var driveRoot = Path.GetPathRoot(scratch)!;
                var parent = RequireBeforeFixture(api.OpenVolumeRoot(driveRoot), handles,
                    "volume root", scratch);
                foreach (var component in scratch[driveRoot.Length..].Split('\\',
                             StringSplitOptions.RemoveEmptyEntries))
                    parent = RequireBeforeFixture(api.OpenDirectoryRelative(parent, component),
                        handles, "ancestor " + component, scratch);
                var root = RequireBeforeFixture(api.CreatePrivateDirectoryRelative(
                    parent, privateName, descriptor), handles, "private root", scratch);
                fixture = new(output, scratch, privateName, descriptor, root);
                fixture._handles.AddRange(handles);
                return fixture;
            }
            catch
            {
                if (fixture is not null)
                    fixture.Dispose();
                else
                    foreach (var handle in handles.AsEnumerable().Reverse())
                        Assert.Equal(FixtureB1NativeJournalCloseDisposition.Closed,
                            api.Close(handle).Disposition);
                output.WriteLine($"direct acquisition failed at {scratch}; path cleanup deferred");
                throw;
            }
        }

        internal IFixtureB1NativeJournalHandle Require(
            FixtureB1NativeJournalHandleResult result,
            string step)
        {
            AdoptReturnedHandle(result);
            Assert.True(result.Succeeded,
                $"{step}: {result.FailureCode} at {ScratchPath}");
            return result.Handle!;
        }

        internal void AdoptReturnedHandle(FixtureB1NativeJournalHandleResult result)
        {
            if (result.Handle is not null &&
                _handles.All(handle => !ReferenceEquals(handle, result.Handle)))
                _handles.Add(result.Handle);
        }

        internal FixtureB1NativeJournalHandleObservation RequireObservation(
            IFixtureB1NativeJournalHandle handle,
            string stage)
        {
            var result = Api.Observe(handle);
            Assert.True(result.Succeeded,
                $"{stage}: {result.FailureCode} at {ScratchPath}");
            return result.Observation!;
        }

        internal FixtureB1NativeJournalCloseResult CloseHandle(
            IFixtureB1NativeJournalHandle handle)
        {
            var result = Api.Close(handle);
            if (result.Disposition == FixtureB1NativeJournalCloseDisposition.Closed)
                _closed.Add(handle.LocalHandleId);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var handle in _handles.AsEnumerable().Reverse())
            {
                if (_closed.Contains(handle.LocalHandleId)) continue;
                var close = CloseHandle(handle);
                _output.WriteLine(
                    $"close {handle.LocalHandleId}: {close.Disposition} {close.FailureCode}");
                Assert.Equal(FixtureB1NativeJournalCloseDisposition.Closed,
                    close.Disposition);
            }
            foreach (var file in KnownFiles)
                DeleteKnownFile(file);
            DeleteKnownDirectory(PrivatePath);
            DeleteKnownDirectory(ScratchPath);
            _output.WriteLine("explicit close: every returned native handle closed exactly once");
        }

        private static IFixtureB1NativeJournalHandle RequireBeforeFixture(
            FixtureB1NativeJournalHandleResult result,
            List<IFixtureB1NativeJournalHandle> handles,
            string step,
            string scratch)
        {
            if (result.Handle is not null)
                handles.Add(result.Handle);
            Assert.True(result.Succeeded,
                $"{step}: {result.FailureCode} at {scratch}");
            return result.Handle!;
        }
    }

    private static void DeleteKnownFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteKnownDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path);
    }

    private sealed class TestWriterAuthority : IFixtureB1NativeJournalWriterAuthority
    {
        public bool AuthorizeLiveWriter(FixtureB1AuthoritySession session, Guid leaseId) =>
            session.ConnectionGeneration == 1 && leaseId != Guid.Empty;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        nint file,
        byte[] buffer,
        uint numberOfBytesToWrite,
        out uint numberOfBytesWritten,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(nint file);
}
