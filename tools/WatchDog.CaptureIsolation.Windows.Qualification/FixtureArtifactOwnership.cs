namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureArtifactKind
{
    Directory,
    File,
}

internal enum FixtureArtifactLifetime
{
    RetainedEvidence,
    Scratch,
}

internal enum FixtureArtifactCreateDisposition
{
    Created,
    OpenedExisting,
    NoCreate,
    Ambiguous,
}

internal enum FixtureArtifactDeletionStatus
{
    Confirmed,
    Present,
    Ambiguous,
}

internal enum FixtureArtifactCloseStatus
{
    Closed,
    StillOpen,
    Ambiguous,
}

internal sealed record FixtureArtifactIdentity(
    ulong Volume,
    ulong FileIdLow,
    ulong FileIdHigh,
    FixtureArtifactKind Kind,
    bool IsReparse);

internal interface IFixtureArtifactHandle
{
    nint RawValue { get; }
}

internal sealed record FixtureArtifactCreateResult(
    FixtureArtifactCreateDisposition Disposition,
    IFixtureArtifactHandle? Handle);

/// <summary>
/// Fixture-only contract. A native adapter must return even an unexpected usable handle on
/// ambiguous results; throwing after an unreported create is cleanup uncertainty.
/// </summary>
internal interface IFixtureArtifactNative
{
    FixtureArtifactCreateResult CreateNew(
        IFixtureArtifactHandle parent, string singleComponentName, FixtureArtifactKind kind);
    FixtureArtifactIdentity Observe(IFixtureArtifactHandle handle);
    bool IsEmptyDirectory(IFixtureArtifactHandle handle, FixtureArtifactIdentity expected);
    bool MarkDelete(IFixtureArtifactHandle handle, FixtureArtifactIdentity expected);
    FixtureArtifactCloseStatus Close(IFixtureArtifactHandle handle);
    // Confirmed requires proof that this exact identity is terminally deleted. Name absence
    // alone is insufficient because a hostile actor could rename the object elsewhere.
    FixtureArtifactDeletionStatus ConfirmTerminalDeletion(
        IFixtureArtifactHandle parent,
        FixtureArtifactIdentity expectedParent,
        string singleComponentName,
        FixtureArtifactIdentity expectedChild);
}

internal sealed class FixtureArtifactContractException(string code) : Exception
{
    internal string Code { get; } = code;
}

/// <summary>
/// Pure fixture ownership model. Not registered with the qualification runner and never calls
/// Windows APIs itself. A later adapter must prove the native status/handle mapping separately.
/// </summary>
internal sealed class FixtureArtifactOwnership
{
    private readonly IFixtureArtifactNative _native;
    private readonly object _gate = new();
    private readonly HashSet<string> _allowedNames;
    private readonly List<Node> _nodes = [];
    private readonly List<IFixtureArtifactHandle> _unexpectedHandles = [];
    private readonly HashSet<nint> _ambiguousCloses = [];
    private bool _childrenReaped;
    private bool _observersDrained;
    private bool _uncertainMutation;
    private bool _stopping;
    private bool _finished;

    internal FixtureArtifactOwnership(
        IFixtureArtifactNative native,
        IFixtureArtifactHandle retainedRoot,
        FixtureArtifactIdentity expectedRoot,
        IEnumerable<string> allowedNames)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        ArgumentNullException.ThrowIfNull(retainedRoot);
        if (!IsUsable(retainedRoot))
            throw new FixtureArtifactContractException("fixture.artifact.root-invalid");
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(allowedNames);
        _allowedNames = new HashSet<string>(StringComparer.Ordinal);
        var caseFoldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allowedNames)
        {
            if (!IsCanonicalName(name) || !caseFoldedNames.Add(name))
                throw new FixtureArtifactContractException("fixture.artifact.allowlist-invalid");
            _allowedNames.Add(name);
        }
        if (!ValidIdentity(expectedRoot, FixtureArtifactKind.Directory, expectedRoot.Volume) ||
            _native.Observe(retainedRoot) != expectedRoot)
            throw new FixtureArtifactContractException("fixture.artifact.root-invalid");
        Root = new Node(retainedRoot, expectedRoot,
            FixtureArtifactLifetime.RetainedEvidence, null, null);
        _nodes.Add(Root);
    }

    internal Node Root { get; }

    // B0 handoff ledger may only derive its directory identity from this retained,
    // currently owned authority, never from an arbitrary caller-supplied file ID.
    internal FixtureArtifactIdentity ObserveRetainedDirectory(Node node)
    {
        lock (_gate)
        {
            if (_stopping || _finished || _uncertainMutation || !_nodes.Contains(node) ||
                node.Terminal || node.Lifetime != FixtureArtifactLifetime.RetainedEvidence ||
                node.Identity.Kind != FixtureArtifactKind.Directory)
                throw new FixtureArtifactContractException("fixture.artifact.authority-invalid");
            FixtureArtifactIdentity observed;
            try { observed = _native.Observe(node.Handle); }
            catch
            {
                _uncertainMutation = true;
                throw;
            }
            if (observed != node.Identity)
            {
                _uncertainMutation = true;
                throw new FixtureArtifactContractException("fixture.artifact.authority-changed");
            }
            return observed;
        }
    }

    internal Node CreateNew(
        Node parent,
        string name,
        FixtureArtifactKind kind,
        FixtureArtifactLifetime lifetime,
        CancellationToken cancellationToken)
    {
        lock (_gate)
            return CreateNewCore(parent, name, kind, lifetime, cancellationToken);
    }

    private Node CreateNewCore(
        Node parent,
        string name,
        FixtureArtifactKind kind,
        FixtureArtifactLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (_stopping || _finished || _uncertainMutation ||
            !_nodes.Contains(parent) || parent.Terminal ||
            !Enum.IsDefined(kind) || !Enum.IsDefined(lifetime) ||
            (lifetime == FixtureArtifactLifetime.RetainedEvidence &&
                parent.Lifetime == FixtureArtifactLifetime.Scratch) ||
            parent.Identity.Kind != FixtureArtifactKind.Directory ||
            !IsCanonicalName(name) || !_allowedNames.Contains(name))
            throw new FixtureArtifactContractException("fixture.artifact.create-invalid");

        cancellationToken.ThrowIfCancellationRequested();
        FixtureArtifactIdentity observedParent;
        try { observedParent = _native.Observe(parent.Handle); }
        catch
        {
            _uncertainMutation = true;
            throw;
        }
        if (observedParent != parent.Identity)
        {
            _uncertainMutation = true;
            throw new FixtureArtifactContractException("fixture.artifact.parent-changed");
        }
        if (!parent.AttemptedNames.Add(name))
            throw new FixtureArtifactContractException("fixture.artifact.create-invalid");
        FixtureArtifactCreateResult result;
        try
        {
            result = _native.CreateNew(parent.Handle, name, kind);
        }
        catch
        {
            _uncertainMutation = true;
            throw;
        }

        if (result.Handle is not null &&
            (!IsUsable(result.Handle) ||
                _nodes.Any(node => node.Handle.RawValue == result.Handle.RawValue) ||
                _unexpectedHandles.Any(handle => handle.RawValue == result.Handle.RawValue)))
        {
            _uncertainMutation = true;
            throw new FixtureArtifactContractException("fixture.artifact.handle-ambiguous");
        }

        if (result.Disposition != FixtureArtifactCreateDisposition.Created || result.Handle is null)
        {
            if (result.Handle is not null)
                _unexpectedHandles.Add(result.Handle);
            if (result.Disposition != FixtureArtifactCreateDisposition.OpenedExisting ||
                result.Handle is null)
                _uncertainMutation = true;
            throw new FixtureArtifactContractException("fixture.artifact.create-not-new");
        }

        FixtureArtifactIdentity identity;
        try
        {
            identity = _native.Observe(result.Handle);
        }
        catch
        {
            _uncertainMutation = true;
            _unexpectedHandles.Add(result.Handle);
            throw;
        }
        if (!ValidIdentity(identity, kind, parent.Identity.Volume) ||
            _nodes.Any(node => node.Identity == identity))
        {
            _uncertainMutation = true;
            _unexpectedHandles.Add(result.Handle);
            throw new FixtureArtifactContractException("fixture.artifact.identity-invalid");
        }

        var created = new Node(result.Handle, identity, lifetime, parent, name);
        _nodes.Add(created);
        cancellationToken.ThrowIfCancellationRequested();
        return created;
    }

    internal void ConfirmChildrenReaped()
    {
        lock (_gate)
            _childrenReaped = true;
    }

    internal void ConfirmObserversDrained()
    {
        lock (_gate)
            _observersDrained = true;
    }

    internal bool TryFinish()
    {
        lock (_gate)
            return TryFinishCore();
    }

    private bool TryFinishCore()
    {
        _stopping = true;
        if (!_childrenReaped || !_observersDrained)
            return false;
        for (var index = _unexpectedHandles.Count - 1; index >= 0; index--)
        {
            var handle = _unexpectedHandles[index];
            if (_ambiguousCloses.Contains(handle.RawValue))
                continue;
            var close = TryClose(handle);
            if (close == FixtureArtifactCloseStatus.Closed)
                _unexpectedHandles.RemoveAt(index);
            else if (close == FixtureArtifactCloseStatus.Ambiguous)
            {
                _ambiguousCloses.Add(handle.RawValue);
                _uncertainMutation = true;
            }
        }

        // Reverse creation order is child-before-parent because parents must be registered first.
        for (var index = _nodes.Count - 1; index >= 0; index--)
        {
            var node = _nodes[index];
            if (node.Terminal)
                continue;
            if ((_uncertainMutation || _unexpectedHandles.Count > 0) &&
                node.Lifetime == FixtureArtifactLifetime.RetainedEvidence)
                continue;
            if (_nodes.Any(child => child.Parent == node && !child.Terminal))
                continue;
            if (node.Lifetime == FixtureArtifactLifetime.Scratch && !node.DeleteMarked)
            {
                if (!TryObserveSame(node))
                {
                    _uncertainMutation = true;
                    continue;
                }
                if (node.Identity.Kind == FixtureArtifactKind.Directory &&
                    !TryIsEmpty(node.Handle, node.Identity))
                    continue;
                if (!TryMarkDelete(node.Handle, node.Identity))
                    continue;
                node.DeleteMarked = true;
            }
            if (!node.HandleClosed && !node.CloseAmbiguous)
            {
                var close = TryClose(node.Handle);
                node.HandleClosed = close == FixtureArtifactCloseStatus.Closed;
                node.CloseAmbiguous = close == FixtureArtifactCloseStatus.Ambiguous;
                if (node.CloseAmbiguous)
                    _uncertainMutation = true;
            }
            if (!node.HandleClosed || node.CloseAmbiguous)
                continue;
            node.Terminal = node.Lifetime != FixtureArtifactLifetime.Scratch ||
                TryConfirmTerminalDeletion(node);
        }

        _finished = _nodes.All(node => node.Terminal) && _unexpectedHandles.Count == 0;
        return _finished && !_uncertainMutation;
    }

    internal static QualificationExitCode ExitCodeAfterCleanup(
        QualificationExitCode requested, bool cleanupConfirmed) =>
        cleanupConfirmed ? requested : QualificationExitCode.CleanupIncomplete;

    private bool TryIsEmpty(IFixtureArtifactHandle handle, FixtureArtifactIdentity expected)
    {
        try { return _native.IsEmptyDirectory(handle, expected); }
        catch { return false; }
    }

    private bool TryObserveSame(Node node)
    {
        try { return _native.Observe(node.Handle) == node.Identity; }
        catch { return false; }
    }

    private bool TryMarkDelete(IFixtureArtifactHandle handle, FixtureArtifactIdentity expected)
    {
        try { return _native.MarkDelete(handle, expected); }
        catch { return false; }
    }

    private bool TryConfirmTerminalDeletion(Node node)
    {
        if (node.Parent is null || node.Name is null)
            return false;
        try
        {
            return _native.ConfirmTerminalDeletion(
                node.Parent.Handle, node.Parent.Identity, node.Name, node.Identity) ==
                FixtureArtifactDeletionStatus.Confirmed;
        }
        catch { return false; }
    }

    private FixtureArtifactCloseStatus TryClose(IFixtureArtifactHandle handle)
    {
        try { return _native.Close(handle); }
        catch { return FixtureArtifactCloseStatus.Ambiguous; }
    }

    private static bool ValidIdentity(
        FixtureArtifactIdentity identity, FixtureArtifactKind kind, ulong expectedVolume) =>
        identity.Volume != 0 && identity.Volume == expectedVolume &&
        (identity.FileIdLow != 0 || identity.FileIdHigh != 0) &&
        identity.Kind == kind && !identity.IsReparse;

    private static bool IsUsable(IFixtureArtifactHandle handle) =>
        handle.RawValue is not 0 and not -1;

    private static bool IsCanonicalName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 120 ||
            name is "." or ".." || name[^1] is '.' or ' ' ||
            name.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
                return false;
        }
        var stem = name.Split('.')[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
            !(stem.Length == 4 && stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) &&
                stem[3] is >= '1' and <= '9') &&
            !(stem.Length == 4 && stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase) &&
                stem[3] is >= '1' and <= '9');
    }

    internal sealed class Node(
        IFixtureArtifactHandle handle,
        FixtureArtifactIdentity identity,
        FixtureArtifactLifetime lifetime,
        Node? parent,
        string? name)
    {
        internal IFixtureArtifactHandle Handle { get; } = handle;
        internal FixtureArtifactIdentity Identity { get; } = identity;
        internal FixtureArtifactLifetime Lifetime { get; } = lifetime;
        internal Node? Parent { get; } = parent;
        internal string? Name { get; } = name;
        internal HashSet<string> AttemptedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal bool DeleteMarked { get; set; }
        internal bool HandleClosed { get; set; }
        internal bool CloseAmbiguous { get; set; }
        internal bool Terminal { get; set; }
    }
}
