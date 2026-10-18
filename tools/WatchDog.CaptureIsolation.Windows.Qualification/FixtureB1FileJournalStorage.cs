using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

public enum FixtureB1FileJournalFault
{
    None,
    RefuseAppend,
    TornAppend,
    WrittenButFalse,
    RefuseAnchor,
    TornAnchor,
    CommittedAnchorButFalse,
}

/// <summary>
/// Fixture-only file store. It supplies managed filesystem evidence to the B1 oracle;
/// it is not a proof of kernel object identity or native durability.
/// </summary>
internal sealed class FixtureB1FileJournalStorage :
    IFixtureB1FramedJournalStore,
    IFixtureB1JournalRootAuthority,
    IDisposable
{
    private const int DigestSize = 32;
    private const int MaxFrameBytes = 4_132;
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private const int AnchorBytes = sizeof(long) + DigestSize;
    private const string FramesName = "frames.bin";
    private const string AnchorName = "anchor.bin";
    private readonly object _gate = new();
    private readonly string _rootPath;
    private readonly string _framesPath;
    private readonly string _anchorPath;
    private readonly Guid _rootIdentity;
    private readonly Guid _fileIdentity;
    private readonly Guid _aclReceipt;
    private readonly Guid _exclusiveWriterReceipt;
    private readonly FileStream _frames;
    private FileStream _anchor;
    private readonly bool _writable;
    private bool _poisoned;
    private bool _disposed;

    private FixtureB1FileJournalStorage(
        string rootPath,
        Guid rootIdentity,
        Guid fileIdentity,
        Guid aclReceipt,
        Guid exclusiveWriterReceipt,
        bool writable,
        FileStream frames,
        FileStream anchor)
    {
        _rootPath = rootPath;
        _framesPath = Path.Combine(rootPath, FramesName);
        _anchorPath = Path.Combine(rootPath, AnchorName);
        _rootIdentity = rootIdentity;
        _fileIdentity = fileIdentity;
        _aclReceipt = aclReceipt;
        _exclusiveWriterReceipt = exclusiveWriterReceipt;
        _writable = writable;
        _frames = frames;
        _anchor = anchor;
    }

    internal static FixtureB1FileJournalStorage Create(
        string directoryPath,
        FixtureB1AuthoritySession session,
        Guid journalId,
        out FixtureB1JournalStorageLease lease)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (journalId == Guid.Empty)
            throw new ArgumentException("A journal identity is required.", nameof(journalId));
        var root = NormalizeRoot(directoryPath);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("A fresh journal root must not preexist.");
        Directory.CreateDirectory(root);
        ApplyPrivateAcl(root);
        if (!IsAdmissibleDirectory(root))
            throw new IOException("The journal root is not a private plain directory.");

        var framesPath = Path.Combine(root, FramesName);
        var anchorPath = Path.Combine(root, AnchorName);
        if (File.Exists(framesPath) || File.Exists(anchorPath))
            throw new IOException("A fresh journal directory must not contain journal files.");
        var frames = OpenExclusive(framesPath, FileMode.CreateNew);
        try
        {
            var anchor = OpenExclusive(anchorPath, FileMode.CreateNew);
            ApplyPrivateAcl(framesPath);
            ApplyPrivateAcl(anchorPath);
            var ids = ComputeIdentities(root, framesPath, anchorPath);
            var storage = new FixtureB1FileJournalStorage(
                root, ids.Root, ids.File, ids.Acl, Guid.NewGuid(), true, frames, anchor);
            lease = storage.CreateLease(journalId, session);
            if (!storage.IsAdmitted(session, lease))
            {
                storage.Dispose();
                throw new IOException("The created journal root failed admission.");
            }
            return storage;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    internal static FixtureB1FileJournalStorage Open(
        string directoryPath,
        FixtureB1AuthoritySession session,
        FixtureB1JournalStorageLease lease)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        var root = NormalizeRoot(directoryPath);
        if (!IsAdmissibleDirectory(root))
            throw new IOException("The journal root is not a private plain directory.");
        var framesPath = Path.Combine(root, FramesName);
        var anchorPath = Path.Combine(root, AnchorName);
        var ids = ComputeIdentities(root, framesPath, anchorPath);
        if (ids.Root != lease.RootIdentity || ids.File != lease.FileIdentity ||
            ids.Acl != lease.AclReceipt)
            throw new IOException("The journal identities no longer match the lease.");
        var frames = OpenForensic(Path.Combine(root, FramesName));
        try
        {
            var anchor = OpenForensic(Path.Combine(root, AnchorName));
            var storage = new FixtureB1FileJournalStorage(
                root, ids.Root, ids.File, ids.Acl,
                lease.ExclusiveWriterReceipt, false, frames, anchor);
            if (!storage.IsAdmitted(session, lease))
            {
                storage.Dispose();
                throw new IOException("The journal root failed admission.");
            }
            return storage;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    internal bool IsPoisoned { get { lock (_gate) return _poisoned; } }
    internal bool IsReadOnly => !_writable;

    internal FixtureB1JournalInspection Inspect(
        FixtureB1AuthorityRegistry? survivingRegistry,
        FixtureB1AuthoritySession session,
        FixtureB1JournalStorageLease lease,
        IFixtureB1JournalPeerAuthority peer) =>
        FixtureB1FramedJournalContract.Inspect(
            survivingRegistry, session, lease, this, peer, this);
    internal FixtureB1FileJournalFault NextFault { get; set; }

    public bool IsAdmitted(
        FixtureB1AuthoritySession session,
        FixtureB1JournalStorageLease lease)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (_disposed || _poisoned ||
                lease.RootIdentity != _rootIdentity || lease.FileIdentity != _fileIdentity ||
                lease.AclReceipt != _aclReceipt || lease.ExclusiveWriterReceipt != _exclusiveWriterReceipt ||
                lease.WrapperInstance != session.WrapperInstance ||
                lease.SessionEpoch != session.SessionEpoch ||
                lease.ConnectionGeneration != session.ConnectionGeneration)
                return false;
            try
            {
                return IsAdmissibleDirectory(_rootPath) &&
                    IsPrivateFile(_framesPath) && IsPrivateFile(_anchorPath) &&
                    ComputeIdentities(_rootPath, _framesPath, _anchorPath) is var ids &&
                    ids.Root == _rootIdentity && ids.File == _fileIdentity && ids.Acl == _aclReceipt &&
                    new FileInfo(_framesPath).Length <= MaxJournalBytes &&
                    new FileInfo(_anchorPath).Length <= MaxJournalBytes;
            }
            catch { return false; }
        }
    }

    public bool TryAppendAndFlush(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            try
            {
                var fault = NextFault;
                NextFault = FixtureB1FileJournalFault.None;
                if (!_writable) return false;
                if (fault == FixtureB1FileJournalFault.RefuseAppend ||
                    !CanWrite(frame.Length) || frame.Length > MaxFrameBytes ||
                    _frames.Length > MaxJournalBytes - frame.Length)
                    return PoisonedFailure();
                _frames.Position = _frames.Length;
                if (fault == FixtureB1FileJournalFault.TornAppend)
                    _frames.Write(frame, 0, Math.Max(1, frame.Length / 2));
                else
                    _frames.Write(frame, 0, frame.Length);
                _frames.Flush(flushToDisk: true);
                return fault == FixtureB1FileJournalFault.None || PoisonedFailure();
            }
            catch { return PoisonedFailure(); }
        }
    }

    public bool TryAdvanceAndFlushAnchor(FixtureB1JournalAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        lock (_gate)
        {
            try
            {
                var fault = NextFault;
                NextFault = FixtureB1FileJournalFault.None;
                if (!_writable) return false;
                if (fault == FixtureB1FileJournalFault.RefuseAnchor ||
                    !CanWrite(AnchorBytes) || anchor.Ordinal <= 0 || anchor.Digest.Length != DigestSize ||
                    _anchor.Length > MaxJournalBytes - AnchorBytes)
                    return PoisonedFailure();
                Span<byte> bytes = stackalloc byte[AnchorBytes];
                BitConverter.TryWriteBytes(bytes, anchor.Ordinal);
                anchor.Digest.CopyTo(bytes[sizeof(long)..]);
                _anchor.Position = _anchor.Length;
                if (fault == FixtureB1FileJournalFault.TornAnchor)
                    _anchor.Write(bytes[..(AnchorBytes / 2)]);
                else
                    _anchor.Write(bytes);
                _anchor.Flush(flushToDisk: true);
                return fault == FixtureB1FileJournalFault.None || PoisonedFailure();
            }
            catch { return PoisonedFailure(); }
        }
    }

    public byte[] ReadFrames()
    {
        lock (_gate)
        {
            EnsureReadable();
            if (_frames.Length > MaxJournalBytes) throw new IOException("Journal bound exceeded.");
            _frames.Position = 0;
            var bytes = new byte[checked((int)_frames.Length)];
            _frames.ReadExactly(bytes);
            return bytes;
        }
    }

    public FixtureB1JournalAnchor? ReadAnchor()
    {
        lock (_gate)
        {
            EnsureReadable();
            if (_anchor.Length == 0) return null;
            if (_anchor.Length % AnchorBytes != 0)
                throw new IOException("Anchor size is invalid.");
            _anchor.Position = _anchor.Length - AnchorBytes;
            Span<byte> bytes = stackalloc byte[AnchorBytes];
            _anchor.ReadExactly(bytes);
            return new(BitConverter.ToInt64(bytes), bytes[sizeof(long)..].ToArray());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _anchor.Dispose();
            _frames.Dispose();
        }
    }

    private bool CanWrite(int bytes) => !_disposed && !_poisoned &&
        IsAdmittedForWrite() && bytes >= 0;

    private bool IsAdmittedForWrite() =>
        _writable && IsAdmissibleDirectory(_rootPath) && IsPrivateFile(_framesPath) && IsPrivateFile(_anchorPath) &&
        new FileInfo(_anchorPath).Length <= MaxJournalBytes &&
        new FileInfo(_anchorPath).Length % AnchorBytes == 0 &&
        ComputeIdentities(_rootPath, _framesPath, _anchorPath) is var ids &&
        ids.Root == _rootIdentity && ids.File == _fileIdentity && ids.Acl == _aclReceipt;

    private void EnsureReadable()
    {
        if (_disposed || !IsAdmissibleDirectory(_rootPath) ||
            !IsPrivateFile(_framesPath) || !IsPrivateFile(_anchorPath))
            throw new IOException("Journal storage is no longer admitted.");
        var ids = ComputeIdentities(_rootPath, _framesPath, _anchorPath);
        if (ids.Root != _rootIdentity || ids.File != _fileIdentity || ids.Acl != _aclReceipt)
            throw new IOException("Journal storage identity or ACL drifted.");
    }

    private bool PoisonedFailure()
    {
        _poisoned = true;
        return false;
    }

    private FixtureB1JournalStorageLease CreateLease(
        Guid journalId, FixtureB1AuthoritySession session) => new(
        journalId, _rootIdentity, _fileIdentity, _aclReceipt,
        _exclusiveWriterReceipt, session.WrapperInstance, session.SessionEpoch,
        session.ConnectionGeneration);

    private static FileStream OpenExclusive(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.WriteThrough | FileOptions.SequentialScan);

    private static FileStream OpenForensic(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
            FileOptions.SequentialScan);

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A root path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsAdmissibleDirectory(string path)
    {
        try
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
            var security = new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            return IsPrivateSecurity(security);
        }
        catch { return false; }
    }

    private static void ApplyPrivateAcl(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ??
            throw new IOException("The current Windows identity is unavailable.");
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current, FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static bool IsPlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static bool IsPrivateFile(string path)
    {
        if (!IsPlainFile(path)) return false;
        return IsPrivateSecurity(new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));
    }

    private static (Guid Root, Guid File, Guid Acl) ComputeIdentities(
        string root, string frames, string anchor)
    {
        var rootId = StableGuid(root + "|" + CreationStamp(root));
        var fileId = StableGuid(frames + "|" + CreationStamp(frames) + "|" +
            anchor + "|" + CreationStamp(anchor));
        var acl = StableGuid(root + "|" + DescribeSecurity(root) + "|" +
            DescribeSecurity(frames) + "|" + DescribeSecurity(anchor));
        return (rootId, fileId, acl);
    }

    private static bool IsPrivateSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        var current = WindowsIdentity.GetCurrent().User;
        if (owner is null || current is null || !owner.Equals(current)) return false;
        var broad = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                broad.Any(sid => sid.Equals(rule.IdentityReference)))
                return false;
        return true;
    }

    private static string DescribeSecurity(string path)
    {
        var security = Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    private static string CreationStamp(string path) =>
        File.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Guid StableGuid(string value) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))[..16]);
}
