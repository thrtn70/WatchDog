using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class WriterRole
{
    internal static async Task<int> RunAsync(ChildOptions options)
    {
        using var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ||
            serverPid != checked((uint)options.ParentPid))
            throw new InvalidDataException("The named-pipe server is not the expected O process.");

        await using var channel = new MessageChannel(pipe);
        await channel.SendAsync(
            new("hello", options.Nonce, Environment.ProcessId),
            CancellationToken.None).ConfigureAwait(false);

        var parentOffer = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(parentOffer, "parent-offer", options.Nonce, options.ParentPid);
        if (parentOffer.Resource != "trusted-parent" || parentOffer.Handle is null or 0 or -1)
            throw new InvalidDataException("O returned an invalid trusted-parent offer.");

        using var parent = new SafeFileHandle(new nint(parentOffer.Handle.Value), ownsHandle: true);
        var parentObservation = NativeMethods.Observe(parent, "trusted-parent");
        NativeMethods.RequireObservation(
            parentObservation,
            NativeMethods.ParentTransferAccess,
            directory: true,
            options.TrustedParentPath);
        await channel.SendAsync(
            new(
                "parent-adopted",
                options.Nonce,
                Environment.ProcessId,
                "trusted-parent",
                parentOffer.Handle,
                parentOffer.Name,
                parentObservation),
            CancellationToken.None).ConfigureAwait(false);

        var parentAck = await ReceiveAsync(channel).ConfigureAwait(false);
        if (options.Scenario == "owner-death-trusted-parent")
            await EnterEarlyNoncooperativeWaitAsync(channel, options, parentAck, "trusted-parent", parentOffer.Handle.Value)
                .ConfigureAwait(false);
        ProtocolChecks.Require(parentAck, "parent-ack", options.Nonce, options.ParentPid);
        if (parentAck.Resource != "trusted-parent")
            throw new InvalidDataException("O ACKed the wrong parent resource.");

        var directorySecurity = CreateSecurityDescriptor(directory: true);
        var fileSecurity = CreateSecurityDescriptor(directory: false);
        using var rootLease = new SourceLease(NativeMethods.CreateDirectoryRelative(
            parent,
            options.PrivateRootName,
            directorySecurity));
        var root = rootLease.Handle;
        using var frames = new SourceLease(NativeMethods.CreateFileRelative(root, "frames.bin", fileSecurity));
        using var anchorLease = new SourceLease(NativeMethods.CreateFileRelative(root, "anchor.bin", fileSecurity));
        var anchor = anchorLease.Handle;

        var rootObservation = NativeMethods.Observe(root, "private-root-source");
        var framesObservation = NativeMethods.Observe(frames.Handle, "frames-source");
        var anchorObservation = NativeMethods.Observe(anchor, "anchor-source");
        var rootPath = Path.Combine(options.TrustedParentPath, options.PrivateRootName);
        NativeMethods.RequireSourceObservation(rootObservation, true, rootPath);
        NativeMethods.RequireSourceObservation(
            framesObservation,
            false,
            Path.Combine(rootPath, "frames.bin"));
        NativeMethods.RequireSourceObservation(
            anchorObservation,
            false,
            Path.Combine(rootPath, "anchor.bin"));
        if (StringComparer.Ordinal.Equals(rootObservation.FileId, framesObservation.FileId) ||
            StringComparer.Ordinal.Equals(rootObservation.FileId, anchorObservation.FileId) ||
            StringComparer.Ordinal.Equals(framesObservation.FileId, anchorObservation.FileId))
            throw new InvalidDataException("W source objects alias before offer.");
        var offers = new[]
        {
            new SourceOffer(
                "private-root",
                root.DangerousGetHandle().ToInt64(),
                options.PrivateRootName,
                rootObservation, rootLease.LeaseId, rootLease.Generation),
            new SourceOffer(
                "frames",
                frames.Handle.DangerousGetHandle().ToInt64(),
                "frames.bin",
                framesObservation, frames.LeaseId, frames.Generation),
            new SourceOffer(
                "anchor",
                anchor.DangerousGetHandle().ToInt64(),
                "anchor.bin",
                anchorObservation, anchorLease.LeaseId, anchorLease.Generation),
        };
        await channel.SendAsync(
            new(
                "source-offers",
                options.Nonce,
                Environment.ProcessId,
                Sources: offers),
            CancellationToken.None).ConfigureAwait(false);

        foreach (var offer in offers)
        {
            var check = await ReceiveAsync(channel).ConfigureAwait(false);
            ProtocolChecks.Require(check, "source-check", options.Nonce, options.ParentPid);
            if (check.Resource != offer.Resource || check.LeaseId != offer.LeaseId ||
                check.Generation != offer.Generation)
                throw new InvalidDataException("O requested an unexpected source lease.");
            var current = offer;
            var lease = offer.Resource switch
            {
                "private-root" => rootLease,
                "frames" => frames,
                "anchor" => anchorLease,
                _ => throw new InvalidDataException("Unexpected lease resource."),
            };
            if (lease.TryCloseWhilePinned())
                throw new InvalidOperationException("Pinned source close was permitted.");
            current = current with { Observation = NativeMethods.Observe(lease.Handle, $"{offer.Resource}-pinned-source") };
            if (offer.Resource == "frames" && options.Scenario == "source-reuse")
            {
                // Deliberately violate the pinned lease for this adversarial scenario only.
                frames.ForceReopen(root);
                current = offer with
                {
                    Handle = frames.Handle.DangerousGetHandle().ToInt64(),
                    LeaseId = frames.LeaseId,
                    Generation = frames.Generation,
                    Observation = NativeMethods.Observe(frames.Handle, "frames-reopened-source"),
                };
            }
            await channel.SendAsync(new("source-state", options.Nonce, Environment.ProcessId,
                Sources: [current], Detail: "close-denied-while-pinned"),
                CancellationToken.None).ConfigureAwait(false);
            var ack = await ReceiveAsync(channel).ConfigureAwait(false);
            if (options.Scenario == $"owner-death-{offer.Resource}")
                await EnterEarlyNoncooperativeWaitAsync(channel, options, ack, offer.Resource,
                    offer.Handle, offer.LeaseId, offer.Generation).ConfigureAwait(false);
            ProtocolChecks.Require(ack, "source-ack", options.Nonce, options.ParentPid);
            if (!StringComparer.Ordinal.Equals(ack.Resource, offer.Resource) ||
                ack.LeaseId != offer.LeaseId || ack.Generation != offer.Generation)
                throw new InvalidDataException($"O ACKed {ack.Resource} while {offer.Resource} was pinned.");
        }

        var shutdown = await ReceiveAsync(channel).ConfigureAwait(false);
        if (options.Scenario is "job-close" or "owner-death")
        {
            ProtocolChecks.Require(shutdown, "noncooperative-wait", options.Nonce, options.ParentPid);
            await channel.SendAsync(new("noncooperative-ready", options.Nonce, Environment.ProcessId),
                CancellationToken.None).ConfigureAwait(false);
            // No pipe reads, parent polling, cancellation, timeout, or cooperative exit remains.
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            throw new InvalidOperationException("Noncooperative wait returned.");
        }
        ProtocolChecks.Require(shutdown, "shutdown", options.Nonce, options.ParentPid);
        await channel.SendAsync(
            new(
                "shutdown-ready",
                options.Nonce,
                Environment.ProcessId,
                Detail: "All three W source originals remain open; no release or write occurred."),
            CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task EnterEarlyNoncooperativeWaitAsync(MessageChannel channel,
        ChildOptions options, WireMessage barrier, string resource, long sourceCoordinate,
        Guid? leaseId = null, long? generation = null)
    {
        ProtocolChecks.Require(barrier, "early-owner-barrier", options.Nonce, options.ParentPid);
        if (barrier.Resource != resource || barrier.Name != options.Scenario ||
            barrier.Handle != sourceCoordinate ||
            barrier.LeaseId != leaseId || barrier.Generation != generation)
            throw new InvalidDataException("The early O-death barrier does not bind the pinned source.");
        await channel.SendAsync(new("noncooperative-ready", options.Nonce, Environment.ProcessId,
            resource, Handle: sourceCoordinate, Name: options.Scenario, Detail: options.Scenario,
            LeaseId: leaseId, Generation: generation),
            CancellationToken.None).ConfigureAwait(false);
        // Original capabilities stay rooted in the caller's using scopes. There are no further
        // pipe reads, parent polls, timeout, cancellation or cooperative exit paths at this edge.
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        throw new InvalidOperationException("Early noncooperative wait returned.");
    }

    private sealed class SourceLease(SafeFileHandle handle) : IDisposable
    {
        internal SafeFileHandle Handle { get; private set; } = handle;
        internal Guid LeaseId { get; private set; } = Guid.NewGuid();
        internal long Generation { get; private set; } = 1;
        private readonly bool _pinned = true;

        internal bool TryCloseWhilePinned()
        {
            if (_pinned)
                return false;
            Handle.Dispose();
            return true;
        }

        internal void ForceReopen(SafeFileHandle root)
        {
            Handle.Dispose();
            Handle = NativeMethods.ReopenFileRelative(root, "frames.bin");
            LeaseId = Guid.NewGuid();
            Generation++;
        }

        public void Dispose() => Handle.Dispose();
    }

    private static async Task<WireMessage> ReceiveAsync(MessageChannel channel)
    {
        using var timeout = new CancellationTokenSource(ProtocolConstants.MessageTimeout);
        return await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
    }

    private static byte[] CreateSecurityDescriptor(bool directory)
    {
        var user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit :
                InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security.GetSecurityDescriptorBinaryForm();
    }
}
