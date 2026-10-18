using System.Security.AccessControl;
using System.Security.Principal;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1NativeJournalObservationNativeTests
{
    [Theory]
    [InlineData("ReFS")]
    [InlineData("exFAT")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedFixedFilesystemNamesFailClosed(string? filesystem) =>
        Assert.False(FixtureB1NativeJournalObservationNative.IsSupportedFilesystem(filesystem));

    [Fact]
    public void NtfsFilesystemNameIsSupported() =>
        Assert.True(FixtureB1NativeJournalObservationNative.IsSupportedFilesystem("ntfs"));

    [Fact]
    public void OptInNativeLeaseRetainsReadObservationAfterWriterClose()
    {
        if (Environment.GetEnvironmentVariable("WATCHDOG_RUN_NATIVE_B1_FIXTURE") != "1")
            return;
        Assert.True(OperatingSystem.IsWindows());

        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wd-b1-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var rootName = "journal-" + Guid.NewGuid().ToString("N");
        var rootPath = System.IO.Path.Combine(scratch, rootName);
        FixtureB1NativeJournalAcquisition? acquisition = null;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!;
            var descriptor = Descriptor(sid);
            var raw = new RawSecurityDescriptor(descriptor, 0);
            var expectedAces = raw.DiscretionaryAcl!.Cast<CommonAce>()
                .Select(ace => new FixtureB1NativeJournalAceObservation(
                    ace.AceQualifier == AceQualifier.AccessAllowed ?
                        FixtureB1NativeJournalAceKind.Allow :
                        FixtureB1NativeJournalAceKind.Deny,
                    ace.SecurityIdentifier.Value, unchecked((uint)ace.AccessMask),
                    (byte)ace.AceFlags)).ToArray();
            var security = new FixtureB1NativeJournalSecurityPolicy(
                descriptor, sid.Value, true, expectedAces);
            var driveRoot = System.IO.Path.GetPathRoot(scratch)!;
            var components = scratch[driveRoot.Length..].Split('\\',
                StringSplitOptions.RemoveEmptyEntries);
            var access = new FixtureB1NativeJournalAccessPolicy(1, 2, 1,
                (uint)(FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile |
                    FixtureB1NativeJournalHandleAccess.AppendDataOrAddSubdirectory |
                    FixtureB1NativeJournalHandleAccess.Delete |
                    FixtureB1NativeJournalHandleAccess.WriteDac |
                    FixtureB1NativeJournalHandleAccess.WriteOwner));
            var options = new FixtureB1NativeJournalAcquisitionOptions(
                driveRoot, components, rootName, "frames.bin", "anchor.bin", 1024,
                1024, security, security, access);
            acquisition = FixtureB1NativeJournalObservation.Acquire(
                FixtureB1NativeJournalObservationNative.Instance,
                new NativeFakeWriterAuthority(),
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
                options);
            Assert.True(acquisition.Succeeded,
                $"acquisition: {acquisition.Failure?.Code} at {scratch}");
            var lease = acquisition.Lease!;
            Assert.True(lease.AuthorizeLiveWriter().Admitted);
            Assert.True(lease.CloseWriterCustody().Complete);
            Assert.False(lease.AuthorizeLiveWriter().Admitted);
            Assert.True(lease.ObserveRetainedStorageForRead().Admitted);
            Assert.True(lease.Close().Complete);
            Assert.False(lease.NativeAuthorityProven);
            Assert.False(lease.NativeDurabilityProven);
        }
        finally
        {
            if (acquisition is not null)
                Assert.True(acquisition.CleanupOwner.CloseAll().Complete);
            if (File.Exists(System.IO.Path.Combine(rootPath, "frames.bin")))
                File.Delete(System.IO.Path.Combine(rootPath, "frames.bin"));
            if (File.Exists(System.IO.Path.Combine(rootPath, "anchor.bin")))
                File.Delete(System.IO.Path.Combine(rootPath, "anchor.bin"));
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath);
            Directory.Delete(scratch);
        }
    }

    [Fact]
    public void OptInIsolatedNativeHandleObservation()
    {
        if (Environment.GetEnvironmentVariable("WATCHDOG_RUN_NATIVE_B1_FIXTURE") != "1")
            return;
        Assert.True(OperatingSystem.IsWindows());

        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wd-b1-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var privateName = "journal-" + Guid.NewGuid().ToString("N");
        var privatePath = System.IO.Path.Combine(scratch, privateName);
        var api = FixtureB1NativeJournalObservationNative.Instance;
        var handles = new List<IFixtureB1NativeJournalHandle>();
        try
        {
            var driveRoot = System.IO.Path.GetPathRoot(scratch)!;
            var parent = Require(api.OpenVolumeRoot(driveRoot), "volume", handles, scratch);
            var components = scratch[driveRoot.Length..].Split('\\',
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
                parent = Require(api.OpenDirectoryRelative(parent, component),
                    "ancestor:" + component, handles, scratch);

            var sid = WindowsIdentity.GetCurrent().User!;
            var descriptor = Descriptor(sid);
            var root = Require(api.CreatePrivateDirectoryRelative(parent, privateName,
                descriptor), "private root", handles, scratch);
            var frames = Require(api.CreateFileRelative(root, "frames.bin", descriptor),
                "frames", handles, scratch);
            var anchor = Require(api.CreateFileRelative(root, "anchor.bin", descriptor),
                "anchor", handles, scratch);
            var framesRead = Require(api.DuplicateReadOnly(frames), "frames read", handles,
                scratch);
            var anchorRead = Require(api.DuplicateReadOnly(anchor), "anchor read", handles,
                scratch);

            var rootObserved = Require(api.Observe(root), "root observation", scratch);
            var framesObserved = Require(api.Observe(frames), "frames observation", scratch);
            var anchorObserved = Require(api.Observe(anchor), "anchor observation", scratch);
            var framesReadObserved = Require(api.Observe(framesRead),
                "frames read observation", scratch);
            var anchorReadObserved = Require(api.Observe(anchorRead),
                "anchor read observation", scratch);
            Assert.Equal(FixtureB1NativeJournalObjectKind.Directory, rootObserved.Kind);
            Assert.Equal(FixtureB1NativeJournalObjectKind.File, framesObserved.Kind);
            Assert.Equal(1u, framesObserved.NumberOfLinks);
            Assert.Equal(1u, anchorObserved.NumberOfLinks);
            Assert.False(framesObserved.DeletePending);
            Assert.False(anchorObserved.DeletePending);
            Assert.Equal(0, framesObserved.EndOfFile);
            Assert.Equal(0, anchorObserved.EndOfFile);
            Assert.True(FixtureB1NativeJournalObservation.SameObject(
                framesObserved, framesReadObserved));
            Assert.True(FixtureB1NativeJournalObservation.SameObject(
                anchorObserved, anchorReadObserved));
            Assert.Equal(sid.Value, framesObserved.Security.OwnerSid);
            Assert.True(framesObserved.Security.DaclProtected);
            Assert.True((framesObserved.GrantedAccessMask & 2) != 0);
            Assert.True((framesReadObserved.GrantedAccessMask & 1) != 0);
            Assert.True((framesReadObserved.GrantedAccessMask & 2) == 0);
            Assert.True((anchorReadObserved.GrantedAccessMask & 2) == 0);
            Assert.Equal(["::$DATA"], framesObserved.StreamNames);
            var entries = api.EnumerateDirectoryEntries(root);
            Assert.True(entries.Succeeded,
                $"enumeration: {entries.FailureCode} at {scratch}");
            Assert.Equal(["anchor.bin", "frames.bin"],
                entries.EntryNames!.Order(StringComparer.Ordinal));
        }
        finally
        {
            foreach (var handle in handles.AsEnumerable().Reverse())
            {
                var close = api.Close(handle);
                Assert.Equal(FixtureB1NativeJournalCloseDisposition.Closed,
                    close.Disposition);
            }
            if (File.Exists(System.IO.Path.Combine(privatePath, "frames.bin")))
                File.Delete(System.IO.Path.Combine(privatePath, "frames.bin"));
            if (File.Exists(System.IO.Path.Combine(privatePath, "anchor.bin")))
                File.Delete(System.IO.Path.Combine(privatePath, "anchor.bin"));
            if (Directory.Exists(privatePath))
                Directory.Delete(privatePath);
            Directory.Delete(scratch);
        }
    }

    private static IFixtureB1NativeJournalHandle Require(
        FixtureB1NativeJournalHandleResult result, string step,
        List<IFixtureB1NativeJournalHandle> handles, string scratch)
    {
        if (result.Handle is not null)
            handles.Add(result.Handle);
        Assert.True(result.Succeeded, $"{step}: {result.FailureCode} at {scratch}");
        return result.Handle!;
    }

    private static FixtureB1NativeJournalHandleObservation Require(
        FixtureB1NativeJournalObservationResult result, string step, string scratch)
    {
        Assert.True(result.Succeeded, $"{step}: {result.FailureCode} at {scratch}");
        return result.Observation!;
    }

    private static byte[] Descriptor(SecurityIdentifier owner)
    {
        var descriptor = new RawSecurityDescriptor(
            $"O:{owner.Value}G:{owner.Value}D:P(A;;FA;;;SY)(A;;FA;;;{owner.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class NativeFakeWriterAuthority : IFixtureB1NativeJournalWriterAuthority
    {
        public bool AuthorizeLiveWriter(FixtureB1AuthoritySession session, Guid leaseId) =>
            session.ConnectionGeneration == 1 && leaseId != Guid.Empty;
    }
}
