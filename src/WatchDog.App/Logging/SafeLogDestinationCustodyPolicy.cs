using System.Collections.Immutable;

namespace WatchDog.App.Logging;

// Pure policy over independently supplied facts. The chain starts at a trusted
// private anchor and ends at the log directory; ancestors outside it are not assessed.
// No filesystem, ACL, or identity lookup occurs here.
internal static class SafeLogDestinationCustodyPolicy
{
    internal static SafeLogCustodyDecision Evaluate(
        SafeLogDestinationIntent? intent, SafeLogDestinationSnapshot? snapshot)
    {
        if (intent is null || snapshot is null)
            return Reject(SafeLogCustodyRejection.MissingEvidence);

        var expected = intent.ExpectedComponentIdentities;
        var observed = snapshot.Components;
        if (string.IsNullOrWhiteSpace(intent.IntendedLogRootIdentity) ||
            string.IsNullOrWhiteSpace(intent.IntendedOwnerIdentity) ||
            expected.IsDefaultOrEmpty || observed.IsDefaultOrEmpty ||
            expected.Any(string.IsNullOrWhiteSpace))
            return Reject(SafeLogCustodyRejection.MissingEvidence);

        if (!StringComparer.Ordinal.Equals(expected[^1], intent.IntendedLogRootIdentity) ||
            expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
            return Reject(SafeLogCustodyRejection.InvalidIntent);
        if (expected.Length != observed.Length)
            return Reject(SafeLogCustodyRejection.ChainMismatch);

        for (var index = 0; index < expected.Length; index++)
        {
            var component = observed[index];
            if (component is null || string.IsNullOrWhiteSpace(component.CanonicalIdentity) ||
                string.IsNullOrWhiteSpace(component.OwnerIdentity))
                return Reject(SafeLogCustodyRejection.MissingEvidence, index);
            if (!StringComparer.Ordinal.Equals(component.CanonicalIdentity, expected[index]))
                return Reject(index == expected.Length - 1
                    ? SafeLogCustodyRejection.RootMismatch : SafeLogCustodyRejection.ChainMismatch, index);
            var expectedParent = index == 0 ? null : expected[index - 1];
            if (!StringComparer.Ordinal.Equals(component.ParentIdentity, expectedParent))
                return Reject(SafeLogCustodyRejection.ChainMismatch, index);
            if (component.Kind == SafeLogCustodyComponentKind.Unknown ||
                component.ReparseState == SafeLogCustodyReparseState.Unknown)
                return Reject(SafeLogCustodyRejection.MissingEvidence, index);
            if (component.Kind != SafeLogCustodyComponentKind.Directory)
                return Reject(SafeLogCustodyRejection.WrongComponentKind, index);
            if (component.ReparseState != SafeLogCustodyReparseState.Absent)
                return Reject(SafeLogCustodyRejection.Redirected, index);
            if (!StringComparer.Ordinal.Equals(component.OwnerIdentity, intent.IntendedOwnerIdentity))
                return Reject(SafeLogCustodyRejection.OwnerMismatch, index);
            var access = CheckOwnerOnlyAccess(component.AccessAssessment,
                component.EffectiveGrants, intent.IntendedOwnerIdentity);
            if (access != SafeLogCustodyRejection.None)
                return Reject(access, index);
        }

        // Directory access does not describe inherit-only rules on a newly created log file.
        if (string.IsNullOrWhiteSpace(snapshot.ProspectiveChildFileOwnerIdentity))
            return Reject(SafeLogCustodyRejection.MissingEvidence, expected.Length - 1);
        if (!StringComparer.Ordinal.Equals(snapshot.ProspectiveChildFileOwnerIdentity,
            intent.IntendedOwnerIdentity))
            return Reject(SafeLogCustodyRejection.OwnerMismatch, expected.Length - 1);
        var childAccess = CheckOwnerOnlyAccess(snapshot.ProspectiveChildFileAccessAssessment,
            snapshot.ProspectiveChildFileEffectiveGrants, intent.IntendedOwnerIdentity);
        if (childAccess != SafeLogCustodyRejection.None)
            return Reject(childAccess, expected.Length - 1);

        return new SafeLogCustodyDecision(true, SafeLogCustodyRejection.None, null);
    }

    private static SafeLogCustodyRejection CheckOwnerOnlyAccess(
        SafeLogCustodyAccessAssessment assessment,
        ImmutableArray<SafeLogEffectiveGrant> grants,
        string? ownerIdentity)
    {
        if (assessment != SafeLogCustodyAccessAssessment.Complete || grants.IsDefault)
            return SafeLogCustodyRejection.IncompleteAccessEvidence;

        var ownerRead = false;
        var ownerWrite = false;
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant.PrincipalIdentity))
                return SafeLogCustodyRejection.IncompleteAccessEvidence;
            if (StringComparer.Ordinal.Equals(grant.PrincipalIdentity, ownerIdentity))
            {
                ownerRead |= grant.CanRead;
                ownerWrite |= grant.CanWriteOrControl;
            }
            else if (grant.CanRead || grant.CanWriteOrControl)
                return SafeLogCustodyRejection.OtherPrincipalAccess;
        }
        return ownerRead && ownerWrite
            ? SafeLogCustodyRejection.None : SafeLogCustodyRejection.OwnerAccessMissing;
    }

    private static SafeLogCustodyDecision Reject(SafeLogCustodyRejection reason, int? index = null) =>
        new(false, reason, index);
}

internal sealed class SafeLogDestinationIntent
{
    internal string? IntendedLogRootIdentity { get; }
    internal string? IntendedOwnerIdentity { get; }
    internal ImmutableArray<string?> ExpectedComponentIdentities { get; }

    internal SafeLogDestinationIntent(string? intendedLogRootIdentity,
        string? intendedOwnerIdentity, IEnumerable<string?>? expectedComponentIdentities)
    {
        IntendedLogRootIdentity = intendedLogRootIdentity;
        IntendedOwnerIdentity = intendedOwnerIdentity;
        ExpectedComponentIdentities = expectedComponentIdentities is null
            ? default : expectedComponentIdentities.ToImmutableArray();
    }
}

internal sealed class SafeLogDestinationSnapshot
{
    internal ImmutableArray<SafeLogCustodyComponent?> Components { get; }
    internal string? ProspectiveChildFileOwnerIdentity { get; }
    internal SafeLogCustodyAccessAssessment ProspectiveChildFileAccessAssessment { get; }
    internal ImmutableArray<SafeLogEffectiveGrant> ProspectiveChildFileEffectiveGrants { get; }

    internal SafeLogDestinationSnapshot(IEnumerable<SafeLogCustodyComponent?>? components,
        SafeLogCustodyAccessAssessment prospectiveChildFileAccessAssessment = SafeLogCustodyAccessAssessment.Unknown,
        IEnumerable<SafeLogEffectiveGrant>? prospectiveChildFileEffectiveGrants = null,
        string? prospectiveChildFileOwnerIdentity = null)
    {
        Components = components is null ? default : components.ToImmutableArray();
        ProspectiveChildFileOwnerIdentity = prospectiveChildFileOwnerIdentity;
        ProspectiveChildFileAccessAssessment = prospectiveChildFileAccessAssessment;
        ProspectiveChildFileEffectiveGrants = prospectiveChildFileEffectiveGrants is null
            ? default : prospectiveChildFileEffectiveGrants.ToImmutableArray();
    }
}

internal sealed class SafeLogCustodyComponent
{
    internal string? CanonicalIdentity { get; }
    internal string? ParentIdentity { get; }
    internal SafeLogCustodyComponentKind Kind { get; }
    internal SafeLogCustodyReparseState ReparseState { get; }
    internal string? OwnerIdentity { get; }
    internal SafeLogCustodyAccessAssessment AccessAssessment { get; }
    internal ImmutableArray<SafeLogEffectiveGrant> EffectiveGrants { get; }

    internal SafeLogCustodyComponent(string? canonicalIdentity, string? parentIdentity,
        SafeLogCustodyComponentKind kind, SafeLogCustodyReparseState reparseState,
        string? ownerIdentity, SafeLogCustodyAccessAssessment accessAssessment,
        IEnumerable<SafeLogEffectiveGrant>? effectiveGrants)
    {
        CanonicalIdentity = canonicalIdentity;
        ParentIdentity = parentIdentity;
        Kind = kind;
        ReparseState = reparseState;
        OwnerIdentity = ownerIdentity;
        AccessAssessment = accessAssessment;
        EffectiveGrants = effectiveGrants is null ? default : effectiveGrants.ToImmutableArray();
    }
}

// CanWriteOrControl includes create, replace, rename, delete, ACL, and ownership authority.
// The future platform adapter must supply effective permissions after group expansion.
internal readonly record struct SafeLogEffectiveGrant(
    string? PrincipalIdentity, bool CanRead, bool CanWriteOrControl);

internal enum SafeLogCustodyComponentKind { Unknown, Directory, Other }
internal enum SafeLogCustodyReparseState { Unknown, Absent, Present }
internal enum SafeLogCustodyAccessAssessment { Unknown, Complete, Incomplete }

internal enum SafeLogCustodyRejection
{
    None,
    MissingEvidence,
    InvalidIntent,
    ChainMismatch,
    RootMismatch,
    WrongComponentKind,
    Redirected,
    OwnerMismatch,
    IncompleteAccessEvidence,
    OtherPrincipalAccess,
    OwnerAccessMissing,
}

internal readonly record struct SafeLogCustodyDecision(
    bool SyntheticPolicySatisfied, SafeLogCustodyRejection Rejection, int? ComponentIndex);
