using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1BirthSlotModelTests
{
    private const long ProcessShell = 101;
    private const long ThreadShell = 102;
    private const long ProcessRaw = 201;
    private const long ThreadRaw = 202;

    [Fact]
    public void CompleteBirthRequiresBothOwnedShellsAndIndependentCloseProof()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.True(slot.ConfirmProcessAttached(ProcessShell));
        Assert.True(slot.TryBeginThreadAttach());
        Assert.True(slot.ConfirmThreadAttached(ThreadShell));
        Assert.False(slot.MayContinueToValidation);

        slot.CompleteOriginalAndCallbacks();
        Assert.True(slot.MayContinueToValidation);
        Assert.False(slot.AdmitClose(false, true, true));
        Assert.False(slot.AdmitClose(true, false, true));
        Assert.False(slot.AdmitClose(true, true, false));
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.False(slot.MayContinueToValidation);
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.False(slot.CleanupConfirmed);
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.CleanupConfirmed);
        Assert.False(slot.MayContinueToValidation);
    }

    [Fact]
    public void StopBeforeLateNativeSuccessStillLetsOriginalReturnOwnBothRawValues()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        slot.Stop();
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.True(slot.ConfirmProcessAttached(ProcessShell));
        Assert.True(slot.TryBeginThreadAttach());
        Assert.True(slot.ConfirmThreadAttached(ThreadShell));
        slot.CompleteOriginalAndCallbacks();

        Assert.False(slot.MayContinueToValidation);
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.True(slot.CleanupConfirmed);
    }

    [Fact]
    public void LateNewRawAfterOriginalCompletesRemainsWithProvider()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        slot.CompleteOriginalAndCallbacks();

        Assert.Equal(FixtureB1BirthAdoptionDisposition.ProviderRetainsNewResource,
            slot.TryAdoptProcessRaw(203));
        Assert.Equal(FixtureB1BirthHandleState.RawOwned, slot.ProcessState);
        Assert.True(slot.Ambiguous);
        Assert.False(slot.MayContinueToValidation);
    }

    [Fact]
    public void MissingSuccessHandleCannotClaimCleanClosure()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        slot.CompleteOriginalAndCallbacks();

        Assert.True(slot.Ambiguous);
        Assert.Equal(FixtureB1BirthHandleState.Empty, slot.ThreadState);
        Assert.False(slot.MayContinueToValidation);
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.Equal(FixtureB1BirthHandleState.Closed, slot.ProcessState);
        Assert.False(slot.CleanupConfirmed);
    }

    [Fact]
    public void PartialShellTransferCanCloseRawFallbackAfterTerminalProof()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.True(slot.ConfirmProcessAttached(ProcessShell));
        slot.CompleteOriginalAndCallbacks();

        Assert.Equal(FixtureB1BirthHandleState.SafeOwned, slot.ProcessState);
        Assert.Equal(FixtureB1BirthHandleState.RawOwned, slot.ThreadState);
        Assert.False(slot.MayContinueToValidation);
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.CleanupConfirmed);
    }

    [Fact]
    public void LiveOriginalOrObserverPreventsCloseAdmission()
    {
        var slot = FullyAdoptedSlot();
        Assert.False(slot.AdmitClose(true, true, true));
        slot.CompleteOriginalAndCallbacks();
        Assert.False(slot.AdmitClose(true, true, false));
        Assert.False(slot.CleanupConfirmed);
    }

    [Fact]
    public void PrematureCloseReceiptIsRejectedAndMakesCleanupAmbiguous()
    {
        var slot = FullyAdoptedSlot();
        Assert.False(slot.ConfirmProcessClosed(true));
        slot.CompleteOriginalAndCallbacks();
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.Ambiguous);
        Assert.False(slot.CleanupConfirmed);
    }

    [Fact]
    public void ReplayedOrAliasedRawNeverCreatesSecondOwner()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.AlreadyOwned,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.AlreadyOwned,
            slot.TryAdoptThreadRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.ProviderRetainsNewResource,
            slot.TryAdoptProcessRaw(203));
        Assert.Equal(FixtureB1BirthHandleState.RawOwned, slot.ProcessState);
        Assert.Equal(FixtureB1BirthHandleState.Empty, slot.ThreadState);
        Assert.True(slot.Ambiguous);
    }

    [Fact]
    public void WrongShellCannotConvertRawOwnerToSafeOwner()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.False(slot.ConfirmProcessAttached(ThreadShell));
        Assert.Equal(FixtureB1BirthHandleState.AttachPending, slot.ProcessState);
        Assert.True(slot.Ambiguous);
    }

    [Fact]
    public void InterruptedAttachNeverLicensesRawFallbackClose()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.False(slot.TryBeginProcessAttach());
        slot.CompleteOriginalAndCallbacks();

        Assert.Equal(FixtureB1BirthHandleState.AttachPending, slot.ProcessState);
        Assert.False(slot.MayContinueToValidation);
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.False(slot.TryBeginProcessClose());
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.False(slot.CleanupConfirmed);
    }

    [Fact]
    public void ProvedFailedAttachRestoresRawFallbackAuthority()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.False(slot.ConfirmProcessAttachFailed(false));
        Assert.False(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessAttachFailed(true));
        slot.CompleteOriginalAndCallbacks();

        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.CleanupConfirmed);
    }

    [Fact]
    public void UnconfirmedCloseAttemptCannotIssueASecondClose()
    {
        var slot = FullyAdoptedSlot();
        slot.CompleteOriginalAndCallbacks();
        Assert.True(slot.AdmitClose(true, true, true));
        Assert.True(slot.TryBeginProcessClose());
        Assert.False(slot.ConfirmProcessClosed(false));
        Assert.Equal(FixtureB1BirthHandleState.ClosePending, slot.ProcessState);
        Assert.False(slot.CleanupConfirmed);
        Assert.False(slot.TryBeginProcessClose());
        Assert.True(slot.ConfirmProcessClosed(true));
        Assert.True(slot.TryBeginThreadClose());
        Assert.True(slot.ConfirmThreadClosed(true));
        Assert.True(slot.CleanupConfirmed);
        Assert.False(slot.ConfirmProcessClosed(true));
        Assert.True(slot.Ambiguous);
    }

    [Fact]
    public void ConcurrentCloseClaimsAllowOnlyOnePhysicalCloseCaller()
    {
        var slot = FullyAdoptedSlot();
        slot.CompleteOriginalAndCallbacks();
        Assert.True(slot.AdmitClose(true, true, true));
        var claims = 0;

        Parallel.For(0, 16, _ =>
        {
            if (slot.TryBeginProcessClose())
                Interlocked.Increment(ref claims);
        });

        Assert.Equal(1, claims);
        Assert.Equal(FixtureB1BirthHandleState.ClosePending, slot.ProcessState);
        Assert.False(slot.CleanupConfirmed);
    }

    [Fact]
    public void DuplicateArmOrNativeSuccessIsRejected()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.False(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.False(slot.MarkNativeSuccess());
        Assert.True(slot.Ambiguous);
    }

    [Fact]
    public void UnarmedOrInvalidRawIsNeverAdopted()
    {
        var slot = NewSlot();
        Assert.Equal(FixtureB1BirthAdoptionDisposition.ProviderRetainsNewResource,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.True(slot.Ambiguous);

        var armed = NewSlot();
        Assert.True(armed.Arm());
        Assert.True(armed.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Invalid,
            armed.TryAdoptProcessRaw(0));
        Assert.Equal(FixtureB1BirthHandleState.Empty, armed.ProcessState);
        Assert.True(armed.Ambiguous);
    }

    private static FixtureB1BirthSlotModel NewSlot() =>
        new(Guid.NewGuid(), ProcessShell, ThreadShell);

    private static FixtureB1BirthSlotModel FullyAdoptedSlot()
    {
        var slot = NewSlot();
        Assert.True(slot.Arm());
        Assert.True(slot.MarkNativeSuccess());
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptProcessRaw(ProcessRaw));
        Assert.Equal(FixtureB1BirthAdoptionDisposition.Adopted,
            slot.TryAdoptThreadRaw(ThreadRaw));
        Assert.True(slot.TryBeginProcessAttach());
        Assert.True(slot.ConfirmProcessAttached(ProcessShell));
        Assert.True(slot.TryBeginThreadAttach());
        Assert.True(slot.ConfirmThreadAttached(ThreadShell));
        return slot;
    }
}
