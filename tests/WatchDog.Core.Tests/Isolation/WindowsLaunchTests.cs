using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class WindowsLaunchTests
{
    [Fact]
    public void AtomicLaunchAbiConstantsAndWin64LayoutsMatchWindowsSdk()
    {
        Assert.Equal((nuint)0x0002000D, WindowsProcessLaunchApi.ProcThreadAttributeJobList);
        Assert.Equal((nuint)0x00020002, WindowsProcessLaunchApi.ProcThreadAttributeHandleList);
        Assert.Equal(0x00000001u, WindowsProcessLaunchApi.HandleFlagInherit);
        Assert.Equal(0x00000004u, WindowsProcessLaunchApi.CreateSuspended);
        Assert.Equal(0x00080000u, WindowsProcessLaunchApi.ExtendedStartupInfoPresent);
        Assert.Equal(0x00000400u, WindowsProcessLaunchApi.CreateUnicodeEnvironment);
        Assert.Equal(0x08000000u, WindowsProcessLaunchApi.CreateNoWindow);
        Assert.Equal(122, WindowsProcessLaunchApi.ErrorInsufficientBuffer);
        Assert.Equal(104, Marshal.SizeOf<WindowsProcessLaunchApi.StartupInfo>());
        Assert.Equal(112, Marshal.SizeOf<WindowsProcessLaunchApi.StartupInfoEx>());
        Assert.Equal(104, OffsetOf<WindowsProcessLaunchApi.StartupInfoEx>(
            nameof(WindowsProcessLaunchApi.StartupInfoEx.AttributeList)));
        Assert.Equal(24, Marshal.SizeOf<WindowsProcessLaunchApi.ProcessInformation>());
        Assert.Equal(16, OffsetOf<WindowsProcessLaunchApi.ProcessInformation>(
            nameof(WindowsProcessLaunchApi.ProcessInformation.ProcessId)));
    }

    [Fact]
    public async Task AttributePlanPreservesLegacyLaunchAndAllowListsOnlyExplicitBootstrapHandles()
    {
        var harness = await CreateHarnessAsync();
        using var planJob = new SafeWindowsHandle((nint)0x500, harness.Containment);
        var legacyPlan = new WindowsLaunchAttributePlan(
            planJob);
        using var section = new SafeFileHandle((nint)0x501, ownsHandle: false);
        using var parent = new SafeFileHandle((nint)0x502, ownsHandle: false);
        var bootstrapPlan = new WindowsLaunchAttributePlan(
            planJob,
            [section, parent]);

        Assert.Equal(1u, legacyPlan.AttributeCount);
        Assert.False(legacyPlan.InheritHandles);
        Assert.Empty(legacyPlan.InheritedChildHandles);
        Assert.Equal(2u, bootstrapPlan.AttributeCount);
        Assert.True(bootstrapPlan.InheritHandles);
        using (var lease = bootstrapPlan.AcquireInheritedHandles())
            Assert.Equal(new nint[] { (nint)0x501, (nint)0x502 }, lease.RawHandles);

        Assert.Throws<HelperBoundaryException>(() => new WindowsLaunchAttributePlan(
            planJob,
            [section, section]));
        Assert.Throws<HelperBoundaryException>(() => new WindowsLaunchAttributePlan(
            planJob,
            [section]));

        await CompleteJobOnlyAsync(harness);
    }

    [Fact]
    public async Task BootstrapLaunchPlanUsesExactlyJobAndHandleListAttributes()
    {
        using var section = new SafeFileHandle((nint)0x601, ownsHandle: false);
        using var parent = new SafeFileHandle((nint)0x602, ownsHandle: false);
        var harness = await CreateHarnessAsync(inheritedChildHandles: [section, parent]);

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(2, harness.Launch.LastAttributeCount);
        Assert.True(harness.Launch.LastInheritHandles);
        Assert.Equal(
            new nint[] { (nint)0x601, (nint)0x602 },
            harness.Launch.LastInheritedHandles);
        Assert.DoesNotContain(harness.Containment.JobHandleValue, harness.Launch.LastInheritedHandles);
        Assert.DoesNotContain(harness.Containment.ProcessHandleValue, harness.Launch.LastInheritedHandles);
        Assert.DoesNotContain(harness.Containment.ThreadHandleValue, harness.Launch.LastInheritedHandles);
        Assert.Equal(
            [
                "attribute.size",
                "attribute.initialize",
                "attribute.job-list",
                "attribute.handle-list",
                "process.create",
                "ownership.capture",
                "process.id",
                "thread.id",
                "process.creation",
            ],
            harness.Launch.Calls);

        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Theory]
    [InlineData(false, 0x08080004u)]
    [InlineData(true, 0x08080404u)]
    public void ProductionCreationFlagsUseExactNoWindowMasks(
        bool explicitEnvironment,
        uint expected)
    {
        Assert.Equal(
            expected,
            WindowsProcessLaunchApi.BuildCreationFlags(explicitEnvironment));
    }

    [Fact]
    public async Task AtomicLaunchUsesExactJobListFlagsAndRetainedHandlesWithoutAssignmentFallback()
    {
        var harness = await CreateHarnessAsync();

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(HelperLaunchOwnershipStatus.CreatedSuspended, result.Status);
        Assert.Equal(harness.Job, result.Job);
        Assert.NotNull(result.Process);
        Assert.Null(result.FailureCode);
        Assert.Equal(harness.Containment.JobHandleValue, harness.Launch.LastJobHandle);
        Assert.Equal((nuint)0x0002000D, harness.Launch.LastAttribute);
        Assert.Equal(1, harness.Launch.LastJobListCount);
        Assert.False(harness.Launch.LastInheritHandles);
        Assert.Equal(0x08080004u, harness.Launch.LastCreationFlags);
        Assert.Equal(
            [
                "attribute.size",
                "attribute.initialize",
                "attribute.job-list",
                "process.create",
                "ownership.capture",
                "process.id",
                "thread.id",
                "process.creation",
            ],
            harness.Launch.Calls);
        Assert.DoesNotContain("job.assign", harness.Containment.Calls);

        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Fact]
    public async Task ExplicitEnvironmentAddsUnicodeFlagAndInputsRemainFrozen()
    {
        var environment = "A=B\0\0";
        var harness = await CreateHarnessAsync(environment);

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(environment, harness.Specification.EnvironmentBlock);
        Assert.Equal(0x08080404u, harness.Launch.LastCreationFlags);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Theory]
    [InlineData("attribute.size")]
    [InlineData("attribute.initialize")]
    [InlineData("attribute.job-list")]
    [InlineData("process.create")]
    public async Task PreCreateFailureReturnsNotCreatedAndLeavesOnlyExactJob(string failedCall)
    {
        var harness = await CreateHarnessAsync();
        harness.Launch.Failures.Add(failedCall);

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, result.Status);
        Assert.Null(result.Process);
        Assert.NotNull(result.FailureCode);
        Assert.Equal(nint.Zero, harness.Containment.ProcessHandleValue);
        await CompleteJobOnlyAsync(harness);
    }

    [Theory]
    [InlineData("attribute.size")]
    [InlineData("attribute.initialize")]
    [InlineData("attribute.job-list")]
    public async Task CancellationBetweenPreCreatePhasesReturnsNotCreated(string cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var harness = await CreateHarnessAsync();
        harness.Launch.CancelAfterCall = cancelAfter;
        harness.Launch.Cancellation = cancellation;

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            cancellation.Token);

        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, result.Status);
        Assert.Null(result.Process);
        Assert.Equal(nint.Zero, harness.Containment.ProcessHandleValue);
        await CompleteJobOnlyAsync(harness);
    }

    [Fact]
    public async Task MismatchedLaunchSpecificationMakesNoNativeCall()
    {
        var harness = await CreateHarnessAsync();
        var mismatched = harness.Request with
        {
            PackageManifestDigest = ProtocolFramingTests.Digest('d'),
        };

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            mismatched,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, result.Status);
        Assert.Empty(harness.Launch.Calls);
        await CompleteJobOnlyAsync(harness);
    }

    [Fact]
    public async Task PreCanceledAtomicLaunchMakesNoNativeCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = await CreateHarnessAsync();

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            cancellation.Token);

        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, result.Status);
        Assert.Empty(harness.Launch.Calls);
        await CompleteJobOnlyAsync(harness);
    }

    [Theory]
    [InlineData("process.id")]
    [InlineData("thread.id")]
    [InlineData("process.creation")]
    public async Task PostCreateQueryFailureReturnsExactOwnedRejectionAndStillCleansUp(
        string failedCall)
    {
        var harness = await CreateHarnessAsync();
        harness.Launch.Failures.Add(failedCall);

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(HelperLaunchOwnershipStatus.CreatedRejected, result.Status);
        Assert.Equal(harness.Job, result.Job);
        Assert.NotNull(result.Process);
        Assert.NotEqual(nint.Zero, harness.Containment.ProcessHandleValue);
        await CompleteCreatedAsync(harness, result.Process!);
        Assert.Equal(harness.Containment.ProcessHandleValue, harness.Launch.LastTerminatedProcess);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("creation")]
    public async Task PostCreateIdentityValidationFailureRemainsOwnedAndRejected(string mismatch)
    {
        var harness = await CreateHarnessAsync();
        if (mismatch == "process")
            harness.Launch.ObservedProcessId++;
        else if (mismatch == "thread")
            harness.Launch.ObservedThreadId++;
        else
            harness.Launch.ObservedCreationFileTime = 0;

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        Assert.Equal(HelperLaunchOwnershipStatus.CreatedRejected, result.Status);
        Assert.Equal("helper.windows.process.identity-mismatch", result.FailureCode);
        Assert.NotNull(result.Process);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterCreationReturnsOwnedRejectionBeforeQueries()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = await CreateHarnessAsync();
        harness.Launch.AfterOwnershipCapture = cancellation.Cancel;

        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            cancellation.Token);

        Assert.Equal(HelperLaunchOwnershipStatus.CreatedRejected, result.Status);
        Assert.NotNull(result.Process);
        Assert.DoesNotContain("process.id", harness.Launch.Calls);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Fact]
    public async Task MembershipMustBeObservedBeforeOriginalThreadCanResume()
    {
        var harness = await CreateHarnessAsync();
        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            harness.Processes.ResumeAsync(result.Process!, CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.process.resume-unverified", exception.FailureCode);
        Assert.Equal(0, harness.Launch.ResumeCalls);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(4294967295L)]
    public async Task ResumeIsAttemptedExactlyOnceAndRejectsEveryCountExceptOne(long resumeResult)
    {
        var harness = await CreateHarnessAsync();
        harness.Launch.ResumeResults.Enqueue(unchecked((uint)resumeResult));
        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);
        _ = await harness.Jobs.ObserveAssignmentAsync(
            harness.Job,
            result.Process!,
            harness.Request.Correlation,
            CancellationToken.None);

        var first = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            harness.Processes.ResumeAsync(result.Process!, CancellationToken.None).AsTask());
        var second = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            harness.Processes.ResumeAsync(result.Process!, CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.process.resume-count-invalid", first.FailureCode);
        Assert.Equal("helper.windows.process.resume-unavailable", second.FailureCode);
        Assert.Equal(1, harness.Launch.ResumeCalls);
        Assert.Equal(harness.Containment.ThreadHandleValue, harness.Launch.LastResumedThread);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Fact]
    public async Task ExactResumeCountOneResumesOriginalThreadOnce()
    {
        var harness = await CreateHarnessAsync();
        var result = await harness.Processes.LaunchSuspendedInJobAsync(
            harness.Request,
            harness.Job,
            CancellationToken.None);
        _ = await harness.Jobs.ObserveAssignmentAsync(
            harness.Job,
            result.Process!,
            harness.Request.Correlation,
            CancellationToken.None);

        await harness.Processes.ResumeAsync(result.Process!, CancellationToken.None);
        var repeated = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            harness.Processes.ResumeAsync(result.Process!, CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.process.resume-unavailable", repeated.FailureCode);
        Assert.Equal(1, harness.Launch.ResumeCalls);
        await CompleteCreatedAsync(harness, result.Process!);
    }

    [Fact]
    public async Task ForeignReservedReapingAndDisposedJobsNeverReachCreateProcess()
    {
        var foreignHarness = await CreateHarnessAsync();
        var foreign = await foreignHarness.Processes.LaunchSuspendedInJobAsync(
            foreignHarness.Request,
            foreignHarness.Job with { OpaqueHandleIdentity = Guid.NewGuid() },
            CancellationToken.None);
        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, foreign.Status);
        Assert.Equal(0, foreignHarness.Launch.CreateCalls);
        await CompleteJobOnlyAsync(foreignHarness);

        var reservedHarness = await CreateHarnessAsync();
        var reservation = reservedHarness.Jobs.ReserveAtomicLaunch(
            reservedHarness.Job,
            reservedHarness.Request.Correlation.HelperInstanceId,
            CancellationToken.None);
        var reserved = await reservedHarness.Processes.LaunchSuspendedInJobAsync(
            reservedHarness.Request,
            reservedHarness.Job,
            CancellationToken.None);
        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, reserved.Status);
        Assert.Equal(0, reservedHarness.Launch.CreateCalls);
        reservedHarness.Jobs.CancelAtomicLaunch(reservation);
        await CompleteJobOnlyAsync(reservedHarness);

        var reapingHarness = await CreateHarnessAsync();
        var reaper = reapingHarness.Jobs.CreateReaper(reapingHarness.Job, null);
        var reaping = await reapingHarness.Processes.LaunchSuspendedInJobAsync(
            reapingHarness.Request,
            reapingHarness.Job,
            CancellationToken.None);
        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, reaping.Status);
        Assert.Equal(0, reapingHarness.Launch.CreateCalls);
        reapingHarness.Containment.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await reapingHarness.Jobs.DisposeAsync();

        var disposedHarness = await CreateHarnessAsync();
        await CompleteJobOnlyAsync(disposedHarness);
        await disposedHarness.Jobs.DisposeAsync();
        var disposed = await disposedHarness.Processes.LaunchSuspendedInJobAsync(
            disposedHarness.Request,
            disposedHarness.Job,
            CancellationToken.None);
        Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, disposed.Status);
        Assert.Equal(0, disposedHarness.Launch.CreateCalls);
    }

    [Fact]
    public async Task InFlightLaunchReservationExcludesConcurrentLaunchReaperAndDisposal()
    {
        using var launchEntered = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        var harness = await CreateHarnessAsync();
        harness.Launch.BeforeCall = call =>
        {
            if (call != "attribute.size")
                return;
            launchEntered.Set();
            releaseLaunch.Wait();
        };
        HelperLaunchOwnershipResult? firstResult = null;
        Exception? firstFailure = null;
        var launchThread = new Thread(() =>
        {
            try
            {
                firstResult = harness.Processes.LaunchSuspendedInJobAsync(
                    harness.Request,
                    harness.Job,
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        })
        {
            IsBackground = true,
        };

        launchThread.Start();
        try
        {
            Assert.True(launchEntered.Wait(TestTimeouts.Signal));
            var competing = await harness.Processes.LaunchSuspendedInJobAsync(
                harness.Request,
                harness.Job,
                CancellationToken.None);
            Assert.Equal(HelperLaunchOwnershipStatus.NotCreated, competing.Status);
            var reaperFailure = Assert.Throws<HelperBoundaryException>(() =>
                harness.Jobs.CreateReaper(harness.Job, null));
            Assert.Equal("helper.windows.reaper.process-required", reaperFailure.FailureCode);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Jobs.DisposeAsync().AsTask());
        }
        finally
        {
            releaseLaunch.Set();
            Assert.True(launchThread.Join(TestTimeouts.Signal));
        }

        Assert.Null(firstFailure);
        Assert.Equal(HelperLaunchOwnershipStatus.CreatedSuspended, firstResult?.Status);
        await CompleteCreatedAsync(harness, firstResult!.Process!);
    }

    private static async Task<LaunchHarness> CreateHarnessAsync(
        string? environment = null,
        IEnumerable<SafeHandle>? inheritedChildHandles = null)
    {
        var request = CaptureIsolationAdmissionFixture.Launch(
            ProtocolFramingTests.Correlation());
        var containment = new FakeWindowsContainmentApi();
        var jobs = new WindowsJobBoundary(containment);
        var job = await jobs.CreateAsync(
            request.Correlation.HelperInstanceId,
            CancellationToken.None);
        var specification = new WindowsLaunchSpecification(
            request,
            @"C:\fixtures\watchdog-helper.exe",
            @"""C:\fixtures\watchdog-helper.exe"" --isolated",
            environment,
            inheritedChildHandles);
        var launch = new FakeWindowsProcessLaunchApi(containment);
        var processes = new WindowsProcessBoundary(jobs, launch, specification);
        return new(request, specification, containment, launch, jobs, processes, job);
    }

    private static async Task CompleteCreatedAsync(
        LaunchHarness harness,
        RetainedProcessIdentity process)
    {
        await harness.Processes.TerminateAsync(process, CancellationToken.None);
        await harness.Jobs.TerminateAsync(harness.Job, CancellationToken.None);
        var reaper = harness.Jobs.CreateReaper(harness.Job, process);
        harness.Containment.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        harness.Containment.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await harness.Jobs.DisposeAsync();
    }

    private static async Task CompleteJobOnlyAsync(LaunchHarness harness)
    {
        var reaper = harness.Jobs.CreateReaper(harness.Job, null);
        harness.Containment.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await harness.Jobs.DisposeAsync();
    }

    private static int OffsetOf<T>(string fieldName) where T : struct =>
        checked((int)Marshal.OffsetOf<T>(fieldName));

    private sealed record LaunchHarness(
        HelperLaunchRequest Request,
        WindowsLaunchSpecification Specification,
        FakeWindowsContainmentApi Containment,
        FakeWindowsProcessLaunchApi Launch,
        WindowsJobBoundary Jobs,
        WindowsProcessBoundary Processes,
        RetainedJobIdentity Job);
}

internal sealed class FakeWindowsProcessLaunchApi(
    FakeWindowsContainmentApi containment) : IWindowsProcessLaunchApi
{
    internal List<string> Calls { get; } = [];
    internal HashSet<string> Failures { get; } = [];
    internal Queue<uint> ResumeResults { get; } = [];
    internal string? CancelAfterCall { get; set; }
    internal CancellationTokenSource? Cancellation { get; set; }
    internal Action? AfterOwnershipCapture { get; set; }
    internal Action<string>? BeforeCall { get; set; }
    internal nint LastJobHandle { get; private set; }
    internal nuint LastAttribute { get; private set; }
    internal int LastAttributeCount { get; private set; }
    internal int LastJobListCount { get; private set; }
    internal bool LastInheritHandles { get; private set; }
    internal IReadOnlyList<nint> LastInheritedHandles { get; private set; } = [];
    internal uint LastCreationFlags { get; private set; }
    internal nint LastResumedThread { get; private set; }
    internal nint LastTerminatedProcess { get; private set; }
    internal int CreateCalls { get; private set; }
    internal int ResumeCalls { get; private set; }
    internal uint ObservedProcessId { get; set; } = 77;
    internal uint ObservedThreadId { get; set; } = 177;
    internal long ObservedCreationFileTime { get; set; } = 123456789;

    public void CreateSuspendedInJob(
        WindowsLaunchSpecification specification,
        SafeWindowsHandle jobHandle,
        IWindowsProcessOwnershipSink ownershipSink,
        CancellationToken cancellationToken)
    {
        var attributePlan = new WindowsLaunchAttributePlan(
            jobHandle,
            specification.InheritedChildHandles);
        Step("attribute.size", cancellationToken);
        Step("attribute.initialize", cancellationToken);
        Step("attribute.job-list", cancellationToken);
        if (attributePlan.InheritHandles)
            Step("attribute.handle-list", cancellationToken);
        LastJobHandle = jobHandle.DangerousGetHandle();
        LastAttribute = WindowsProcessLaunchApi.ProcThreadAttributeJobList;
        LastAttributeCount = checked((int)attributePlan.AttributeCount);
        LastJobListCount = 1;
        LastInheritHandles = attributePlan.InheritHandles;
        LastInheritedHandles = attributePlan.InheritedChildHandles
            .Select(static handle => handle.DangerousGetHandle())
            .ToArray();
        LastCreationFlags = WindowsProcessLaunchApi.BuildCreationFlags(
            specification.EnvironmentBlock is not null);
        Step("process.create", cancellationToken);
        CreateCalls++;
        containment.CaptureCreatedProcess(ownershipSink);
        Calls.Add("ownership.capture");
        AfterOwnershipCapture?.Invoke();
    }

    public uint GetProcessId(SafeWindowsHandle processHandle)
    {
        Calls.Add("process.id");
        MaybeFail("process.id");
        return ObservedProcessId;
    }

    public uint GetThreadId(SafeWindowsHandle threadHandle)
    {
        Calls.Add("thread.id");
        MaybeFail("thread.id");
        return ObservedThreadId;
    }

    public long GetProcessCreationFileTime(SafeWindowsHandle processHandle)
    {
        Calls.Add("process.creation");
        MaybeFail("process.creation");
        return ObservedCreationFileTime;
    }

    public uint ResumePrimaryThread(SafeWindowsHandle threadHandle)
    {
        Calls.Add("thread.resume");
        ResumeCalls++;
        LastResumedThread = threadHandle.DangerousGetHandle();
        return ResumeResults.Count == 0 ? 1 : ResumeResults.Dequeue();
    }

    public void TerminateProcess(SafeWindowsHandle processHandle)
    {
        Calls.Add("process.terminate");
        LastTerminatedProcess = processHandle.DangerousGetHandle();
        MaybeFail("process.terminate");
    }

    private void Step(string call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeCall?.Invoke(call);
        Calls.Add(call);
        MaybeFail(call);
        if (string.Equals(CancelAfterCall, call, StringComparison.Ordinal))
            Cancellation?.Cancel();
    }

    private void MaybeFail(string call)
    {
        if (Failures.Contains(call))
            throw new HelperBoundaryException($"helper.windows.fake.{call.Replace('.', '-')}-failed");
    }
}
