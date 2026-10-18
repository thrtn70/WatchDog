using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Input;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;

namespace WatchDog.Integration.Tests;

public sealed class TutorialCaptureControllerTests
{
    public static IEnumerable<object[]> ConfirmationReentryCases =>
        from controllerObserver in new[] { false, true }
        from freshConfirmation in new[] { false, true }
        from action in new[] { "revoke", "replace", "same-plan", "close", "dispose", "foreign" }
        select new object[] { controllerObserver, freshConfirmation, action };

    [Theory]
    [MemberData(nameof(ConfirmationReentryCases))]
    public async Task AdapterConfirmationReentryPreservesNewestIntent(bool controllerObserver, bool freshConfirmation, string action)
    {
        await using var h = new Harness();
        var controller = h.Manager.TutorialCapture;
        using var commands = new TutorialWorkspaceCapture(controller, startAdmitted: true);
        using var foreign = new TutorialWorkspaceCapture(controller, startAdmitted: true);
        commands.Confirm(h.Plan());
        var seedRevision = controller.State.ConfirmationRevision;
        var outer = h.Plan();
        var replacement = action == "same-plan" ? outer : h.Plan();
        var called = false;
        Exception? callbackFailure = null;
        Task<bool>? close = null;
        long? replacementRevision = null;
        void Observe()
        {
            var state = controller.State;
            if (called || !AtConfirmationBoundary(state, seedRevision, freshConfirmation)) return;
            called = true;
            try
            {
                switch (action)
                {
                    case "revoke": commands.Revoke(); break;
                    case "replace":
                    case "same-plan": commands.Confirm(replacement); replacementRevision = controller.State.ConfirmationRevision; break;
                    case "foreign": foreign.Confirm(replacement); replacementRevision = controller.State.ConfirmationRevision; break;
                    case "close": close = commands.CloseAsync(); break;
                    case "dispose": commands.Dispose(); break;
                }
            }
            catch (Exception ex) { callbackFailure = ex; }
        }
        if (controllerObserver) controller.StateChanged += Observe;
        else commands.StateChanged += Observe;
        try { commands.Confirm(outer); }
        finally
        {
            if (controllerObserver) controller.StateChanged -= Observe;
            else commands.StateChanged -= Observe;
        }
        Assert.True(called);
        Assert.Null(callbackFailure);
        if (replacementRevision is { } expectedRevision)
        {
            Assert.Equal(expectedRevision, controller.State.ConfirmationRevision);
            var owner = action == "foreign" ? foreign : commands;
            if (action == "foreign") Assert.Null(await commands.StartAsync());
            Assert.True(owner.StartAvailable);
            var result = await owner.StartAsync();
            Assert.NotNull(result);
            Assert.True(result.StartTransition.Succeeded);
            Assert.False(result.Revoked);
            Assert.Same(replacement.Request, Assert.Single(h.Backend.Starts).Request);
            await owner.StopAsync();
            Assert.Same(replacement.Request, Assert.Single(h.Backend.Stops).Request);
        }
        else
        {
            if (close is not null) Assert.True(await close);
            Assert.False(commands.StartAvailable);
            Assert.False(controller.State.ConfirmationAvailable);
            Assert.Null(await commands.StartAsync());
            Assert.Empty(h.Backend.Starts);
        }
    }

    public static IEnumerable<object[]> ConfirmationReentryStartCases =>
        from controllerObserver in new[] { false, true }
        from freshConfirmation in new[] { false, true }
        from foreignOwner in new[] { false, true }
        from pending in new[] { false, true }
        select new object[] { controllerObserver, freshConfirmation, foreignOwner, pending };

    [Theory]
    [MemberData(nameof(ConfirmationReentryStartCases))]
    public async Task AdapterStaleConfirmationCannotDisturbReentrantCapture(bool controllerObserver, bool freshConfirmation, bool foreignOwner, bool pending)
    {
        await using var h = new Harness();
        var controller = h.Manager.TutorialCapture;
        using var commands = new TutorialWorkspaceCapture(controller, startAdmitted: true);
        using var foreign = new TutorialWorkspaceCapture(controller, startAdmitted: true);
        var owner = foreignOwner ? foreign : commands;
        commands.Confirm(h.Plan());
        var seedRevision = controller.State.ConfirmationRevision;
        var replacement = h.Plan();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        CancellationToken submittedToken = default;
        h.Backend.OnStart = async (context, token) =>
        {
            submittedToken = token;
            entered.TrySetResult(true);
            if (pending) await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        var called = false;
        Exception? callbackFailure = null;
        Task<TutorialStartResult?>? start = null;
        long? replacementRevision = null;
        CaptureSessionContext? acquired = null;
        void Observe()
        {
            if (called || !AtConfirmationBoundary(controller.State, seedRevision, freshConfirmation)) return;
            called = true;
            try
            {
                owner.Confirm(replacement);
                replacementRevision = controller.State.ConfirmationRevision;
                start = owner.StartAsync();
                acquired = owner.RetainedCleanupContext;
            }
            catch (Exception ex) { callbackFailure = ex; }
        }
        if (controllerObserver) controller.StateChanged += Observe;
        else commands.StateChanged += Observe;
        try
        {
            commands.Confirm(h.Plan());
            Assert.True(called);
            Assert.Null(callbackFailure);
            Assert.NotNull(start);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(replacementRevision, controller.State.ConfirmationRevision);
            Assert.Same(replacement, owner.ActivePlan);
            Assert.False(submittedToken.IsCancellationRequested);
            Assert.Empty(h.Backend.Stops);
            if (pending) Assert.False(start.IsCompleted);
            else
            {
                Assert.NotNull(acquired);
                Assert.Same(acquired, owner.RetainedCleanupContext);
            }
        }
        finally
        {
            if (controllerObserver) controller.StateChanged -= Observe;
            else commands.StateChanged -= Observe;
            release.TrySetResult(true);
        }
        var result = await start!;
        Assert.NotNull(result);
        Assert.True(result.StartTransition.Succeeded);
        Assert.False(result.Revoked);
        Assert.Same(replacement.Request, Assert.Single(h.Backend.Starts).Request);
        Assert.Empty(h.Backend.Stops);
        await owner.StopAsync();
        Assert.Same(replacement.Request, Assert.Single(h.Backend.Stops).Request);
        Assert.False(Assert.Single(h.Backend.StopTokens).CanBeCanceled);
    }

    private static bool AtConfirmationBoundary(TutorialCaptureState state, long seedRevision, bool freshConfirmation) =>
        freshConfirmation
            ? state.ConfirmationRevision > seedRevision && state.ConfirmationAvailable
            : state.ConfirmationRevision == seedRevision && !state.ConfirmationAvailable;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterCancellationCallbackPreservesNewConfirmationAndExactPendingCleanup(bool throwAfterConfirmation)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var pendingPlan = h.Plan();
        var replacement = h.Plan();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        CancellationTokenRegistration registration = default;
        CaptureSessionContext? pendingContext = null;
        var callbacks = 0;
        long? replacementRevision = null;
        h.Backend.OnStart = async (context, token) =>
        {
            pendingContext = context;
            registration = token.Register(() =>
            {
                callbacks++;
                if (callbacks != 1) return;
                commands.Confirm(replacement);
                replacementRevision = h.Manager.TutorialCapture.State.ConfirmationRevision;
                if (throwAfterConfirmation) throw new InvalidOperationException("Synthetic cancellation observer failure.");
            });
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        commands.Confirm(pendingPlan);
        var pending = commands.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (throwAfterConfirmation) Assert.Throws<AggregateException>(() => commands.Confirm(h.Plan()));
            else commands.Confirm(h.Plan());
            Assert.Equal(1, callbacks);
            Assert.NotNull(replacementRevision);
            Assert.Equal(replacementRevision, h.Manager.TutorialCapture.State.ConfirmationRevision);
            Assert.Same(pendingPlan, commands.ActivePlan);
        }
        finally { release.TrySetResult(true); }
        var result = await pending;
        registration.Dispose();
        Assert.NotNull(result);
        Assert.True(result.Revoked);
        Assert.Equal(pendingContext, Assert.Single(h.Backend.Stops));
        Assert.Same(pendingPlan.Request, h.Backend.Stops[0].Request);
        Assert.False(Assert.Single(h.Backend.StopTokens).CanBeCanceled);
        Assert.True(commands.StartAvailable);
        h.Backend.OnStart = null;
        var replacementResult = await commands.StartAsync();
        Assert.NotNull(replacementResult);
        Assert.True(replacementResult.StartTransition.Succeeded);
        Assert.False(replacementResult.Revoked);
        Assert.Same(replacement.Request, h.Backend.Starts[1].Request);
        await commands.StopAsync();
        Assert.Equal(2, h.Backend.Stops.Count);
        Assert.Same(replacement.Request, h.Backend.Stops[1].Request);
    }

    [Fact]
    public async Task AdapterNullConfirmationDoesNotRevokeAcceptedIntent()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var plan = h.Plan();
        commands.Confirm(plan);
        Assert.Throws<ArgumentNullException>(() => commands.Confirm(null!));
        Assert.True(commands.StartAvailable);
        await commands.StartAsync();
        Assert.Same(plan.Request, Assert.Single(h.Backend.Starts).Request);
        await commands.StopAsync();
    }

    [Fact]
    public async Task RegionDispatchPreservesConfirmedTargetWithoutResolvingAgain()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var mayResolve = true;
        RegionCaptureTarget? frozen = null;
        var selection = new TutorialRegionSelection(_ => true, (id, bounds) => mayResolve
            ? frozen = new(id, bounds, new(-1920, 0, 1920, 1080))
            : throw new InvalidOperationException("No reconstruction during Start."), _ => true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: selection);
        ConfirmWorkspaceRegion(vm);
        var plan = vm.Plan!;
        Assert.Same(frozen, plan.Request.Target);
        mayResolve = false;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Same(plan.Request, Assert.Single(h.Backend.Starts).Request);
        Assert.Same(frozen, h.Backend.Starts[0].RequestedTarget);
        Assert.Equal(0, h.Engine.RegionResolutions);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Single(h.Backend.Stops);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("region")]
    [InlineData("throw")]
    public async Task RegionStartRejectsChangedOwnerOrFrozenBoundary(string failure)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var unavailable = false;
        var selection = new TutorialRegionSelection(_ => true,
            (id, bounds) => new(id, bounds, new(-1920, 0, 1920, 1080)),
            _ => !unavailable || (failure == "throw" ? throw new InvalidOperationException(@"C:\private\region") : failure != "region"));
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => !unavailable || failure != "owner", regionSelection: selection);
        ConfirmWorkspaceRegion(vm);
        unavailable = true;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(h.Backend.Starts);
        Assert.Null(vm.Plan);
        Assert.False(vm.StartAvailable);
        Assert.DoesNotContain(@"C:\private", vm.StatusText);
    }

    [Fact]
    public async Task ProductionRegionConfirmationCannotBypassAdmission()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture);
        var selection = RegionSelection();
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: selection);
        ConfirmWorkspaceRegion(vm);
        Assert.False(vm.StartAvailable);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(await commands.StartAsync());
        Assert.Empty(h.Backend.Starts);
        Assert.Empty(h.Backend.Stops);
    }

    [Fact]
    public async Task RegionPlanNotificationCannotPublishRevokedCoordinates()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: RegionSelection());
        var changed = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (!changed && args.PropertyName == nameof(vm.Plan) && vm.Plan?.Request.Target is RegionCaptureTarget)
            {
                changed = true;
                vm.RegionXText = "-";
            }
        };
        PrepareWorkspaceRegion(vm);
        vm.ConfirmRegionCommand.Execute(null);
        Assert.True(changed);
        Assert.Null(vm.Plan);
        Assert.False(commands.StartAvailable);
        Assert.Null(await commands.StartAsync());
        Assert.Empty(h.Backend.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionStartCallbackCannotDispatchChangedOrReconfirmedDraft(bool confirmNewer)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        TutorialWorkspaceViewModel? vm = null;
        var mutate = false;
        var changed = false;
        var selection = new TutorialRegionSelection(_ => true,
            (id, bounds) => new(id, bounds, new(-1920, 0, 1920, 1080)), _ =>
            {
                if (mutate && !changed)
                {
                    changed = true;
                    vm!.RegionXText = "-200";
                    if (confirmNewer) vm.ConfirmRegionCommand.Execute(null);
                    else vm.RegionXText = "-100";
                }
                return true;
            });
        using (vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: selection))
        {
            ConfirmWorkspaceRegion(vm);
            mutate = true;
            await vm.StartCommand.ExecuteAsync(null);
            Assert.True(changed);
            Assert.Empty(h.Backend.Starts);
            if (confirmNewer)
            {
                Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(vm.Plan!.Request.Target).PhysicalBounds.X);
                await vm.StartCommand.ExecuteAsync(null);
                Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(Assert.Single(h.Backend.Starts).RequestedTarget).PhysicalBounds.X);
                await vm.StopCommand.ExecuteAsync(null);
            }
            else
                Assert.Null(vm.Plan);
        }
    }

    [Theory]
    [InlineData(true, "edit")]
    [InlineData(true, "replace")]
    [InlineData(true, "close")]
    [InlineData(true, "dispose")]
    [InlineData(false, "edit")]
    [InlineData(false, "replace")]
    [InlineData(false, "close")]
    [InlineData(false, "dispose")]
    public async Task ConfirmationPresentationRetainsOnlyCurrentAuthority(bool region, string action)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: RegionSelection());
        var changed = false;
        Task<bool>? close = null;
        TutorialRecordingPlan? replacement = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (!changed && args.PropertyName == nameof(vm.StartAvailable) && commands.StartAvailable)
            {
                changed = true;
                switch (action)
                {
                    case "edit":
                        vm.RegionXText = "-";
                        break;
                    case "replace":
                        PrepareWorkspaceRegion(vm);
                        vm.RegionXText = "-200";
                        vm.ConfirmRegionCommand.Execute(null);
                        replacement = vm.Plan;
                        break;
                    case "close":
                        close = vm.RequestCloseAsync();
                        break;
                    case "dispose":
                        vm.Dispose();
                        break;
                }
            }
        };
        if (region)
        {
            PrepareWorkspaceRegion(vm);
            vm.ConfirmRegionCommand.Execute(null);
        }
        else
        {
            var display = WorkspaceDisplay("display");
            vm.OfferDisplays([display]);
            vm.SelectedDisplay = display;
            vm.ConfirmDisplayCommand.Execute(null);
        }
        Assert.True(changed);
        if (action == "replace")
        {
            Assert.NotNull(replacement);
            Assert.Same(replacement, vm.Plan);
            Assert.True(commands.StartAvailable);
            await commands.StartAsync();
            Assert.Same(replacement.Request, Assert.Single(h.Backend.Starts).Request);
            Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(replacement.Request.Target).PhysicalBounds.X);
            await commands.StopAsync();
        }
        else
        {
            if (close is not null) Assert.True(await close);
            if (action == "edit") Assert.Null(vm.Plan);
            Assert.False(commands.StartAvailable);
            Assert.Null(await commands.StartAsync());
            Assert.Empty(h.Backend.Starts);
        }
    }

    [Theory]
    [InlineData(false, "clear", false)]
    [InlineData(false, "clear", true)]
    [InlineData(false, "plan", false)]
    [InlineData(false, "plan", true)]
    [InlineData(true, "clear", false)]
    [InlineData(true, "clear", true)]
    [InlineData(true, "plan", false)]
    [InlineData(true, "plan", true)]
    public async Task SourceConfirmationNotificationsCannotOverwriteNewerRegionIntent(bool window, string notification, bool replace)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            _ => true, commands, _ => true, RegionSelection());
        if (window)
        {
            var option = new WatchDog.Native.Win32.WindowEnumerator.WindowInfo(new IntPtr(123), "Tutorial", "tutorial.exe", 42, "Fixture");
            vm.OfferWindows([option]);
            vm.SelectedWindow = option;
        }
        else
            PrepareWorkspaceRegion(vm);
        var changed = false;
        TutorialRecordingPlan? newer = null;
        vm.PropertyChanged += (_, args) =>
        {
            var triggered = notification == "clear"
                ? args.PropertyName == nameof(vm.RegionXText)
                : args.PropertyName == nameof(vm.Plan) && vm.Plan is not null;
            if (changed || !triggered) return;
            changed = true;
            if (replace)
            {
                PrepareWorkspaceRegion(vm);
                vm.RegionXText = "-200";
                vm.ConfirmRegionCommand.Execute(null);
                newer = vm.Plan;
            }
            else
                vm.RegionXText = "-";
        };
        if (window) vm.ConfirmWindowCommand.Execute(null);
        else vm.ConfirmDisplayCommand.Execute(null);
        Assert.True(changed);
        if (replace)
        {
            Assert.NotNull(newer);
            Assert.Same(newer, vm.Plan);
            await commands.StartAsync();
            Assert.Same(newer.Request, Assert.Single(h.Backend.Starts).Request);
            Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(newer.Request.Target).PhysicalBounds.X);
            await commands.StopAsync();
        }
        else
        {
            Assert.Null(vm.Plan);
            Assert.False(commands.StartAvailable);
            Assert.Null(await commands.StartAsync());
            Assert.Empty(h.Backend.Starts);
        }
    }

    [Fact]
    public async Task RegionStartPresentationCannotSubstituteNewerConfirmation()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: RegionSelection());
        ConfirmWorkspaceRegion(vm);
        var changed = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (!changed && args.PropertyName == nameof(vm.StartAvailable))
            {
                changed = true;
                vm.RegionXText = "-200";
                vm.ConfirmRegionCommand.Execute(null);
            }
        };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(changed);
        Assert.Empty(h.Backend.Starts);
        Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(vm.Plan!.Request.Target).PhysicalBounds.X);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(-200, Assert.IsType<RegionCaptureTarget>(Assert.Single(h.Backend.Starts).RequestedTarget).PhysicalBounds.X);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task PendingRegionEditRetainsAcquiredBoundaryUntilFailedCleanupIsRetried()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true, regionSelection: RegionSelection());
        ConfirmWorkspaceRegion(vm);
        var owned = vm.Plan;
        var exactSource = vm.ExactWindowText;
        var pending = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.RegionXText = "-200";
            vm.ConfirmRegionCommand.Execute(null);
            h.Backend.FailStop = true;
        }
        finally { release.TrySetResult(true); }
        await pending;
        Assert.Same(owned, vm.ActivePlan);
        Assert.Equal(exactSource, vm.ExactWindowText);
        Assert.Contains("-100", vm.RegionAvailabilityText);
        Assert.DoesNotContain("-200", vm.RegionAvailabilityText);
        Assert.False(vm.CanEditPlan);
        Assert.True(vm.StopAvailable);
        Assert.Same(owned!.Request, Assert.Single(h.Backend.Stops).Request);
        Assert.False(Assert.Single(h.Backend.StopTokens).CanBeCanceled);
        h.Backend.FailStop = false;
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Null(commands.RetainedCleanupContext);
        Assert.Single(h.Backend.Starts);
    }

    private static TutorialRegionSelection RegionSelection() => new(_ => true,
        (id, bounds) => new(id, bounds, new(-1920, 0, 1920, 1080)), _ => true);

    private static void PrepareWorkspaceRegion(TutorialWorkspaceViewModel vm)
    {
        var owner = WorkspaceDisplay("display");
        vm.OfferDisplays([owner]);
        vm.SelectedDisplay = owner;
        vm.RegionXText = "-100";
        vm.RegionYText = "20";
        vm.RegionWidthText = "50";
        vm.RegionHeightText = "50";
    }

    private static void ConfirmWorkspaceRegion(TutorialWorkspaceViewModel vm)
    {
        PrepareWorkspaceRegion(vm);
        vm.ConfirmRegionCommand.Execute(null);
        Assert.IsType<RegionCaptureTarget>(vm.Plan!.Request.Target);
    }

    [Fact]
    public async Task ConfirmedDisplayDispatchesFrozenSelectedTargetInsteadOfSavedMonitor()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var option = WorkspaceDisplay("explicit-display");
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: target => target == option.Target);
        ConfirmWorkspaceDisplay(vm, option);
        var plan = vm.Plan!;
        Assert.NotEqual(h.Defaults.Capture.MonitorStableId, option.Target.StableDisplayId);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Same(plan.Request, Assert.Single(h.Backend.Starts).Request);
        Assert.Same(option.Target, plan.Request.Target);
        Assert.Null(plan.Request.Webcam);
        Assert.Equal(0, h.Settings.Loads);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Single(h.Backend.Stops);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("id")]
    [InlineData("index")]
    [InlineData("name")]
    [InlineData("throw")]
    public async Task DisplayStartRevalidatesFullIdentityWithoutFallback(string failure)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        var option = WorkspaceDisplay("display");
        DisplayCaptureTarget? current = option.Target;
        var unavailable = false;
        var catalog = new DisplayCaptureCatalog(() => [], _ => throw new InvalidOperationException("No name fallback."),
            _ => unavailable && failure == "throw" ? throw new InvalidOperationException(@"C:\private\display") : current);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: catalog.IsCurrent);
        ConfirmWorkspaceDisplay(vm, option);
        current = failure switch
        {
            "missing" => null,
            "id" => option.Target with { StableDisplayId = "another-display" },
            "index" => option.Target with { MonitorIndex = 2 },
            "name" => option.Target with { DisplayName = "Another display" },
            _ => option.Target,
        };
        unavailable = true;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(h.Backend.Starts);
        Assert.Empty(h.Backend.Stops);
        Assert.Null(vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.False(vm.StartAvailable);
        Assert.DoesNotContain(@"C:\private", vm.StatusText);
    }

    [Fact]
    public async Task DisplayRefreshRevokesPendingIntentBeforeEnumerationCanCompleteStart()
    {
        await using var h = new Harness();
        var entered = Gate<CancellationToken>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, token) =>
        {
            entered.TrySetResult(token);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true);
        ConfirmWorkspaceDisplay(vm, WorkspaceDisplay("display-a"));
        var pending = vm.StartCommand.ExecuteAsync(null);
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revokedBeforeEnumeration = false;
        try
        {
            vm.RefreshDisplays(() =>
            {
                revokedBeforeEnumeration = token.IsCancellationRequested;
                release.TrySetResult(true);
                return [WorkspaceDisplay("display-b")];
            });
        }
        finally { release.TrySetResult(true); }
        await pending;
        Assert.True(revokedBeforeEnumeration);
        Assert.True(h.Manager.TutorialCapture.LastStartResult!.Revoked);
        Assert.Single(h.Backend.Stops);
        Assert.Null(commands.RetainedCleanupContext);
        Assert.Null(vm.Plan);
    }

    [Fact]
    public async Task ProductionDisplayConfirmationCannotBypassAdmission()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true);
        ConfirmWorkspaceDisplay(vm, WorkspaceDisplay("display"));
        Assert.False(vm.StartAvailable);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(await commands.StartAsync());
        Assert.Empty(h.Backend.Starts);
        Assert.Empty(h.Backend.Stops);
    }

    [Fact]
    public async Task PendingDisplayRefreshRetainsAcquiredSourceDetailsUntilFailedCleanupIsRetried()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            capture: commands, isDisplayCurrent: _ => true);
        var first = WorkspaceDisplay("display-a");
        ConfirmWorkspaceDisplay(vm, first);
        var ownedPlan = vm.Plan;
        var exactSource = vm.ExactWindowText;
        var boundary = vm.BoundaryText;
        var pending = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            ConfirmWorkspaceDisplay(vm, WorkspaceDisplay("display-b"));
            h.Backend.FailStop = true;
        }
        finally { release.TrySetResult(true); }
        await pending;
        Assert.Equal(first.Target, Assert.Single(h.Backend.Starts).RequestedTarget);
        Assert.Same(ownedPlan, vm.ActivePlan);
        Assert.Equal(exactSource, vm.ExactWindowText);
        Assert.Contains("display-a", vm.ExactWindowText);
        Assert.DoesNotContain("display-b", vm.ExactWindowText);
        Assert.Contains("submitted recording", vm.DisplayAvailabilityText);
        Assert.Equal(boundary, vm.BoundaryText);
        Assert.NotNull(commands.RetainedCleanupContext);
        Assert.False(vm.CanEditPlan);
        Assert.False(vm.StartAvailable);
        Assert.True(vm.StopAvailable);
        Assert.Equal(first.Target, Assert.Single(h.Backend.Stops).RequestedTarget);
        Assert.False(Assert.Single(h.Backend.StopTokens).CanBeCanceled);
        h.Backend.FailStop = false;
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Null(commands.RetainedCleanupContext);
        Assert.Single(h.Backend.Starts);
        Assert.All(h.Backend.Stops, context => Assert.Equal(first.Target, context.RequestedTarget));
    }

    private static DisplayCaptureOption WorkspaceDisplay(string id) =>
        new(new(id, "Display", 1), $"DEVICE-{id}", 1920, 1080, true);

    private static void ConfirmWorkspaceDisplay(TutorialWorkspaceViewModel vm, DisplayCaptureOption option)
    {
        vm.OfferDisplays([option]);
        vm.SelectedDisplay = option;
        vm.ConfirmDisplayCommand.Execute(null);
        Assert.NotNull(vm.Plan);
    }

    [Fact]
    public async Task WorkspaceProductionGateRejectsDirectStartWithoutBackendCalls()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture);
        commands.Confirm(h.Plan());
        Assert.False(commands.StartAvailable);
        Assert.Null(await commands.StartAsync());
        Assert.Empty(h.Backend.Starts);
        Assert.Empty(h.Backend.Stops);
        Assert.True(await commands.CloseAsync());
    }

    [Fact]
    public async Task PendingRevocationWithFailedCleanupWaitsForExplicitRetry()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        commands.Confirm(h.Plan());
        var pending = commands.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { h.Backend.FailStop = true; commands.Revoke(); }
        finally { release.TrySetResult(true); }
        var result = await pending;
        Assert.True(result!.Revoked);
        Assert.NotNull(result.RetainedCleanupContext);
        Assert.Single(h.Backend.Stops);
        Assert.True(commands.StopAvailable);
        h.Backend.FailStop = false;
        await commands.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAtControllerCompletionStillReconcilesSubmittedCapture(bool failCleanup)
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        h.Backend.FailStop = failCleanup;
        var cancelled = false;
        h.Manager.TutorialCapture.StateChanged += () =>
        {
            var state = h.Manager.TutorialCapture.State;
            if (!cancelled && commands.CancelAvailable && state.RetainedCleanupContext is not null && !state.IsOperationInFlight)
            {
                cancelled = true;
                vm.CancelCommand.Execute(null);
            }
        };
        var result = await commands.StartAsync();
        Assert.True(cancelled);
        Assert.True(result!.Revoked);
        Assert.True(result.StartTransition.Succeeded);
        Assert.NotNull(result.ReconciliationStopTransition);
        Assert.Single(h.Backend.Stops);
        Assert.False(Assert.Single(h.Backend.StopTokens).CanBeCanceled);
        Assert.Equal(failCleanup, result.RetainedCleanupContext is not null);
        Assert.Equal(failCleanup, commands.StopAvailable);
        h.Backend.FailStop = false;
        if (failCleanup) await commands.StopAsync();
    }

    [Fact]
    public async Task ConnectedWorkspaceUsesFrozenPlanOnceAndStopsItsExactContext()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        var plan = vm.Plan!;
        Assert.True(vm.StartAvailable);
        await ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        Assert.Same(plan.Request, Assert.Single(h.Backend.Starts).Request);
        Assert.True(vm.StopAvailable);
        Assert.False(vm.StartAvailable);
        Assert.Null(await commands.StartAsync());
        await ((IAsyncRelayCommand)vm.StopCommand).ExecuteAsync(null);
        Assert.Single(h.Backend.Stops);
        Assert.Null(commands.RetainedCleanupContext);
        Assert.False(vm.StartAvailable);
        Assert.Equal(0, h.Settings.Loads);
    }

    [Fact]
    public async Task WorkspaceCloseWaitsForRevokedLateStartAndUncancelledCleanup()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        var start = ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = vm.RequestCloseAsync();
        try { Assert.False(close.IsCompleted); }
        finally { release.TrySetResult(true); }
        await start;
        Assert.True(await close);
        Assert.True(h.Manager.TutorialCapture.LastStartResult!.Revoked);
        Assert.Single(h.Backend.Stops);
        Assert.False(Assert.Single(h.Backend.StopTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task WorkspaceFailedCloseRetainsCleanupForRetry()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        await ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        var expected = commands.RetainedCleanupContext;
        h.Backend.FailStop = true;
        Assert.False(await vm.RequestCloseAsync());
        Assert.Same(expected, commands.RetainedCleanupContext);
        Assert.True(vm.StopAvailable);
        h.Backend.FailStop = false;
        Assert.True(await vm.RequestCloseAsync());
        Assert.Null(commands.RetainedCleanupContext);
        Assert.All(h.Backend.Stops, context => Assert.Equal(expected, context));
    }

    [Fact]
    public async Task StaleWorkspaceCannotRevokeOrStopNewerWorkspace()
    {
        await using var h = new Harness();
        using var old = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        old.Confirm(h.Plan());
        await old.StartAsync();
        await old.StopAsync();
        using var current = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        current.Confirm(h.Plan());
        old.Revoke();
        Assert.True(current.StartAvailable);
        await current.StartAsync();
        var context = current.RetainedCleanupContext;
        var stopCount = h.Backend.Stops.Count;
        Assert.True(await old.CloseAsync());
        old.Dispose();
        Assert.Same(context, current.RetainedCleanupContext);
        Assert.Equal(stopCount, h.Backend.Stops.Count);
        await current.StopAsync();
    }

    [Fact]
    public async Task ScopedStopRejectsOldRevisionUnderControllerOwnershipLock()
    {
        await using var h = new Harness();
        var controller = h.Manager.TutorialCapture;
        var old = controller.Confirm(h.Plan());
        await controller.StartAsync(old);
        await controller.StopAsync(old);
        var current = controller.Confirm(h.Plan());
        await controller.StartAsync(current);
        var context = controller.RetainedCleanupContext;
        var result = await controller.StopAsync(old);
        Assert.Equal(CaptureFailureKind.Superseded, result.Failure!.Kind);
        Assert.Same(context, controller.RetainedCleanupContext);
        Assert.Single(h.Backend.Stops);
        await controller.StopAsync(current);
    }

    [Fact]
    public async Task ControllerNotificationsExposeCommittedStateAndIsolateObservers()
    {
        await using var h = new Harness();
        var controller = h.Manager.TutorialCapture;
        var completedStart = Gate<TutorialCaptureState>();
        controller.StateChanged += () => throw new InvalidOperationException("Untrusted observer");
        controller.StateChanged += () =>
        {
            var state = controller.State;
            if (state.RetainedCleanupContext is not null && !state.IsOperationInFlight)
                completedStart.TrySetResult(state);
        };
        var revision = controller.Confirm(h.Plan());
        await controller.StartAsync(revision);
        Assert.Equal(revision, (await completedStart.Task.WaitAsync(TimeSpan.FromSeconds(5))).RetainedRevision);
        await controller.StopAsync(revision);
        Assert.Null(controller.State.RetainedRevision);
        Assert.Null(controller.State.RetainedCleanupContext);
        Assert.False(controller.State.IsOperationInFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceStartRevalidatesExactWindowAndFailsClosedOnUnavailableValidation(bool throws)
    {
        await using var h = new Harness();
        var current = true;
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle),
            _ => current || (throws ? throw new InvalidOperationException(@"C:\private\identity") : false), commands);
        ConfirmWorkspaceWindow(vm);
        current = false;
        await ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        Assert.Empty(h.Backend.Starts);
        Assert.False(vm.StartAvailable);
        Assert.DoesNotContain(@"C:\private", vm.StatusText);
    }

    [Fact]
    public async Task WorkspaceRefreshDuringPendingStartRevokesWithoutLosingAcquiredCleanup()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        var start = ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { vm.OfferWindows([]); Assert.Null(vm.Plan); }
        finally { release.TrySetResult(true); }
        await start;
        Assert.True(h.Manager.TutorialCapture.LastStartResult!.Revoked);
        Assert.Null(commands.RetainedCleanupContext);
        Assert.Single(h.Backend.Stops);
        Assert.False(vm.StartAvailable);
    }

    [Fact]
    public async Task RevokedLateStartWithFailedCleanupShowsOwnedPlanInsteadOfEditedDraft()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        var owned = vm.Plan;
        var boundary = vm.BoundaryText;
        var audio = vm.AudioText;
        var output = vm.OutputText;
        var exactWindow = vm.ExactWindowText;
        var start = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.OfferWindows([]);
            vm.IncludeCamera = true;
            h.Backend.FailStop = true;
        }
        finally { release.TrySetResult(true); }
        await start;
        Assert.Null(vm.Plan);
        Assert.Same(owned, vm.ActivePlan);
        Assert.Equal(boundary, vm.BoundaryText);
        Assert.Equal(audio, vm.AudioText);
        Assert.Equal(output, vm.OutputText);
        Assert.Equal(exactWindow, vm.ExactWindowText);
        Assert.Equal("Camera off for the submitted recording.", vm.CameraText);
        Assert.False(vm.CanEditPlan);
        Assert.True(vm.StopAvailable);
        h.Backend.FailStop = false;
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ConnectedWorkspaceWithoutExactWindowValidatorCannotDispatch()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), capture: commands);
        ConfirmWorkspaceWindow(vm);
        await ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        Assert.Empty(h.Backend.Starts);
        Assert.False(vm.StartAvailable);
    }

    [Fact]
    public async Task ActiveWorkspaceCannotSilentlyEditItsRecordingSourceOrCameraConsent()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        using var vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        ConfirmWorkspaceWindow(vm);
        var plan = vm.Plan;
        await ((IAsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);
        vm.SelectedWindow = null;
        vm.IncludeCamera = true;
        vm.OfferWindows([]);
        Assert.Same(plan, vm.Plan);
        Assert.False(vm.IncludeCamera);
        Assert.True(vm.StopAvailable);
        await ((IAsyncRelayCommand)vm.StopCommand).ExecuteAsync(null);
    }

    [Fact]
    public async Task ForeignWorkspaceConfirmationDoesNotRevokePendingStart()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var owner = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        owner.Confirm(h.Plan());
        var start = owner.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var foreign = new TutorialWorkspaceCapture(h.Manager.TutorialCapture, startAdmitted: true);
        try
        {
            foreign.Confirm(h.Plan());
            Assert.False(foreign.StartAvailable);
            Assert.True(await foreign.CloseAsync());
        }
        finally { release.TrySetResult(true); }
        Assert.False((await start)!.Revoked);
        Assert.Empty(h.Backend.Stops);
        await owner.StopAsync();
    }

    [Fact]
    public async Task QueuedWorkspaceInvalidationCannotPublishAfterDisposal()
    {
        await using var h = new Harness();
        using var commands = new TutorialWorkspaceCapture(h.Manager.TutorialCapture);
        var context = new QueuedWorkspaceContext();
        var previous = SynchronizationContext.Current;
        TutorialWorkspaceViewModel vm;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            vm = new TutorialWorkspaceViewModel(h.Baseline, new TutorialLifecycleObservation(h.Lifecycle), _ => true, commands);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        using (vm)
        {
            var notifications = 0;
            vm.PropertyChanged += (_, _) => notifications++;
            commands.Confirm(h.Plan());
            Assert.NotEmpty(context.Pending);
            vm.Dispose();
            var afterDispose = notifications;
            while (context.Pending.TryDequeue(out var callback)) callback();
            Assert.Equal(afterDispose, notifications);
        }
    }

    private sealed class QueuedWorkspaceContext : SynchronizationContext
    {
        public System.Collections.Concurrent.ConcurrentQueue<Action> Pending { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Pending.Enqueue(() => callback(state));
    }

    private static void ConfirmWorkspaceWindow(TutorialWorkspaceViewModel vm)
    {
        var window = new WatchDog.Native.Win32.WindowEnumerator.WindowInfo(new IntPtr(123), "Tutorial", "tutorial.exe", 42, "Fixture");
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        Assert.NotNull(vm.Plan);
    }

    public static IEnumerable<object[]> Targets =>
    [
        [new WindowCaptureTarget(42, new IntPtr(123), "Tutorial", "tutorial.exe", "Fixture")],
        [new DisplayCaptureTarget("display", "Display", 1)],
        [new RegionCaptureTarget("display", new(-100, 20, 80, 60), new(-1920, 0, 1920, 1080))],
    ];

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ConnectedDispatchPreservesEntireRequestAndStopDoesNotReacquire(CaptureTarget target)
    {
        await using var h = new Harness();
        var plan = h.Plan(target);
        var controller = h.Manager.TutorialCapture;
        Assert.Same(controller, h.Manager.TutorialCapture);
        var result = await controller.StartAsync(controller.Confirm(plan));
        Assert.True(result.StartTransition.Succeeded);
        Assert.False(result.Revoked);
        Assert.Null(result.ReconciliationStopTransition);
        var started = Assert.Single(h.Backend.Starts);
        Assert.Same(plan.Request, started.Request);
        Assert.Null(started.Request.Webcam);
        Assert.Same(plan.Request, result.RetainedCleanupContext!.Request);
        Assert.Equal(0, h.Settings.Loads);
        Assert.Equal(0, h.Engine.RegionResolutions);
        var stop = await controller.StopAsync();
        Assert.True(stop.Succeeded);
        Assert.Same(stop, controller.LastStopTransition);
        Assert.Null(controller.RetainedCleanupContext);
        Assert.Single(h.Backend.Starts);
        Assert.Single(h.Backend.Stops);
    }

    [Fact]
    public async Task ConsumedRevisionCannotSubmitTwiceEvenAfterStop()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var revision = c.Confirm(h.Plan());
        Assert.True((await c.StartAsync(revision)).StartTransition.Succeeded);
        await c.StopAsync();
        Assert.Equal(CaptureCommandDisposition.Rejected, (await c.StartAsync(revision)).StartTransition.Disposition);
        Assert.Single(h.Backend.Starts);
    }

    [Fact]
    public async Task RevokedLateSuccessRetainsStartAndReconcilesWithoutCancelledToken()
    {
        await using var h = new Harness();
        using var cancel = new CancellationTokenSource();
        var entered = Gate<CaptureSessionContext>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        {
            entered.TrySetResult(context);
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        var c = h.Manager.TutorialCapture;
        var revision = c.Confirm(h.Plan());
        var pending = c.StartAsync(revision, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var duplicate = await c.StartAsync(revision);
            Assert.False(duplicate.StartTransition.Succeeded);
            Assert.True(c.IsOperationInFlight);
        }
        finally { release.TrySetResult(true); }
        var result = await pending;
        Assert.True(result.StartTransition.Succeeded);
        Assert.True(result.Revoked);
        Assert.True(result.ReconciliationStopTransition!.Succeeded);
        Assert.Null(result.RetainedCleanupContext);
        Assert.Single(h.Backend.Starts);
        Assert.False(Assert.Single(h.Backend.StopTokens).IsCancellationRequested);
        Assert.Same(result, c.LastStartResult);
    }

    [Fact]
    public async Task FailedCleanupReceiptAndExactContextSurviveConfirmationReplacementForRetry()
    {
        await using var h = new Harness();
        h.Backend.FailStop = true;
        var c = h.Manager.TutorialCapture;
        var start = await c.StartAsync(c.Confirm(h.Plan()));
        var expected = start.RetainedCleanupContext!;
        c.Confirm(h.Plan(new DisplayCaptureTarget("display", "Display", 1)));
        var failed = await c.StopAsync();
        Assert.Equal(CaptureCommandDisposition.Failed, failed.Disposition);
        Assert.Same(failed, c.LastStopTransition);
        Assert.Same(expected, c.RetainedCleanupContext);
        h.Backend.FailStop = false;
        Assert.True((await c.StopAsync()).Succeeded);
        Assert.All(h.Backend.Stops, context => Assert.Equal(expected, context));
        Assert.Null(c.RetainedCleanupContext);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task UnavailableSourceRejectsBeforeDispatch(CaptureTarget target)
    {
        await using var h = new Harness();
        h.Engine.Available = false;
        var c = h.Manager.TutorialCapture;
        var result = await c.StartAsync(c.Confirm(h.Plan(target)));
        Assert.False(result.StartTransition.Succeeded);
        Assert.Equal(CaptureFailureKind.SourceLost, result.StartTransition.Failure!.Kind);
        Assert.Empty(h.Backend.Starts);
    }

    [Fact]
    public async Task DisplayFullRecordDriftRejectsBeforeDispatch()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var revision = c.Confirm(h.Plan(new DisplayCaptureTarget("display", "Old name", 1)));
        var result = await c.StartAsync(revision);
        Assert.Equal(CaptureFailureKind.SourceLost, result.StartTransition.Failure!.Kind);
        Assert.Empty(h.Backend.Starts);
    }

    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ExplicitCameraIdentityIsNotReplacedBySavedPreference()
    {
        await using var h = new Harness();
        var camera = new WebcamCaptureSelection("confirmed-camera", "Confirmed", 640, 480, 25, "MJPEG", 900);
        var plan = h.Plan(camera: camera);
        var result = await h.Manager.TutorialCapture.StartAsync(h.Manager.TutorialCapture.Confirm(plan));
        Assert.True(result.StartTransition.Succeeded);
        Assert.Same(camera, Assert.Single(h.Backend.Starts).Request.Webcam);
        Assert.Equal(0, h.Settings.Loads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("automatic")]
    [InlineData("replay")]
    [InlineData("target")]
    public async Task MalformedTypedPlanCannotBypassTutorialAdmission(string invalid)
    {
        await using var h = new Harness();
        var plan = h.Plan();
        var request = plan.Request;
        var expectation = request.StartupExpectation!;
        var changed = new CaptureRequest(request.RequestId, request.RequestedAt,
            invalid == "automatic" ? CaptureInitiation.Automatic : request.Initiation,
            invalid == "target" ? new WindowCaptureTarget(0, IntPtr.Zero) : request.Target,
            startPolicy: invalid == "replay" ? h.Baseline.Baseline!.DefaultStartPolicy : request.StartPolicy,
            startupExpectation: invalid == "missing" ? null : invalid == "invalid"
                ? new(0, expectation.HostBaselineId, expectation.StartupCanvas, expectation.StartupAudio,
                    expectation.TrackLayoutVersion, expectation.StartupOnlyConstraints, expectation.CanonicalSaveRoot)
                : expectation);
        // Ordinary planners cannot create these values. Deliberately corrupt only the input
        // record to pin the headless boundary's required shape checks independently of UI.
        typeof(TutorialRecordingPlan).GetField("<Request>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(plan, changed);
        var c = h.Manager.TutorialCapture;
        var result = await c.StartAsync(c.Confirm(plan));
        Assert.Equal(CaptureFailureKind.InvalidRequest, result.StartTransition.Failure!.Kind);
        Assert.Empty(h.Backend.Starts);
        Assert.Equal(0, h.Engine.Revalidations);
    }

    [Fact]
    public async Task ClosedAdmissionRejectsBeforeSourceCheck()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var revision = c.Confirm(h.Plan());
        await h.Lifecycle.ShutdownAsync();
        Assert.Equal(CaptureFailureKind.AdmissionClosed, (await c.StartAsync(revision)).StartTransition.Failure!.Kind);
        Assert.Equal(0, h.Engine.Revalidations);
        Assert.Empty(h.Backend.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeDispatchConsumesRevisionAndHasNoBackendEffects(bool duringSourceCheck)
    {
        await using var h = new Harness();
        using var cancel = new CancellationTokenSource();
        var c = h.Manager.TutorialCapture;
        var revision = c.Confirm(h.Plan());
        if (duringSourceCheck) h.Engine.BeforeRevalidation = cancel.Cancel;
        else cancel.Cancel();
        var result = await c.StartAsync(revision, cancel.Token);
        Assert.Equal(CaptureFailureKind.Cancelled, result.StartTransition.Failure!.Kind);
        Assert.True(result.Revoked);
        Assert.Null(result.ReconciliationStopTransition);
        Assert.Empty(h.Backend.Starts);
        Assert.False((await c.StartAsync(revision)).StartTransition.Succeeded);
    }

    [Fact]
    public async Task QueuedAutomaticAcquisitionIsCancelledImmediatelyBeforeTutorialBegin()
    {
        await using var h = new Harness();
        var reached = Gate<bool>();
        var release = Gate<bool>();
        var game = new GameInfo { ExecutableName = "game.exe", DisplayName = "Game", ProcessId = 41, ProcessStartedAt = DateTimeOffset.UnixEpoch };
        h.Detector.Emit(game);
        h.Engine.BeforeGameResolution = () => { reached.TrySetResult(true); release.Task.GetAwaiter().GetResult(); };
        var automatic = Task.Run(() => h.Manager.HandleGameStartedAsync(game));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var c = h.Manager.TutorialCapture;
            Assert.True((await c.StartAsync(c.Confirm(h.Plan()))).StartTransition.Succeeded);
        }
        finally { release.TrySetResult(true); }
        await automatic;
        Assert.Equal(CaptureInitiation.Manual, Assert.Single(h.Backend.Starts).Request.Initiation);
        await h.Manager.TutorialCapture.StopAsync();
        Assert.Single(h.Backend.Starts);
        // A later real detection is still allowed: tutorial Stop is not a global pause.
        h.Engine.BeforeGameResolution = null;
        await h.Manager.HandleGameStartedAsync(game);
        Assert.Equal(CaptureInitiation.Automatic, h.Backend.Starts[1].Request.Initiation);
    }

    [Fact]
    public async Task RefreshDuringStartPreservesNewPlanButWaitsForOldExactCleanup()
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        { entered.TrySetResult(true); await release.Task; return CaptureBackendStartResult.Success(context.RequestedTarget); };
        var c = h.Manager.TutorialCapture;
        var pending = c.StartAsync(c.Confirm(h.Plan()));
        long replacement;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            replacement = c.Confirm(h.Plan());
            Assert.Equal(CaptureFailureKind.InvalidState, (await c.StartAsync(replacement)).StartTransition.Failure!.Kind);
        }
        finally { release.TrySetResult(true); }
        var result = await pending;
        Assert.True(result.Revoked);
        Assert.True(result.StartTransition.Succeeded);
        Assert.True(result.ReconciliationStopTransition!.Succeeded);
        h.Backend.OnStart = null;
        Assert.True((await c.StartAsync(replacement)).StartTransition.Succeeded);
        Assert.Equal(2, h.Backend.Starts.Count);
    }

    [Fact]
    public async Task RevokedFailedStartRetainsUnapprovedActualAndActualFailedStopForRetry()
    {
        await using var h = new Harness();
        var actual = new DisplayCaptureTarget("unapproved", "Unapproved", 9);
        var c = h.Manager.TutorialCapture;
        h.Backend.FailStop = true;
        h.Backend.OnStart = (context, _) =>
        {
            c.Revoke();
            return Task.FromResult(CaptureBackendStartResult.Failed(new(CaptureFailureKind.Backend,
                "fixture.partial-start", "Synthetic retained start."), actual));
        };
        var plan = h.Plan();
        var result = await c.StartAsync(c.Confirm(plan));
        Assert.Equal("fixture.partial-start", result.StartTransition.Failure!.Code);
        Assert.Equal("fixture.stop", result.ReconciliationStopTransition!.Failure!.Code);
        Assert.True(result.Revoked);
        var context = result.RetainedCleanupContext!;
        Assert.Equal(plan.Request.Target, context.RequestedTarget);
        Assert.Equal(actual, context.ActualTarget);
        Assert.Same(context, c.RetainedCleanupContext);
        Assert.Same(result.ReconciliationStopTransition, c.LastStopTransition);
        Assert.All(h.Backend.StopTokens, token => Assert.False(token.IsCancellationRequested));
        Assert.False((await c.StartAsync(c.Confirm(h.Plan()))).StartTransition.Succeeded);
        h.Backend.FailStop = false;
        Assert.True((await c.StopAsync()).Succeeded);
        Assert.All(h.Backend.Stops, stopped => Assert.Equal(context, stopped));
    }

    [Fact]
    public async Task SuccessfulButUnapprovedActualIsRetainedAfterCoordinatorCleanupFailure()
    {
        await using var h = new Harness();
        var actual = new WindowCaptureTarget(42, new IntPtr(123), "Changed title", "tutorial.exe", "Fixture");
        h.Backend.OnStart = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(actual));
        h.Backend.FailStop = true;
        var c = h.Manager.TutorialCapture;
        var result = await c.StartAsync(c.Confirm(h.Plan()));
        Assert.Equal("capture.start.unapproved-target", result.StartTransition.Failure!.Code);
        Assert.Equal(actual, result.RetainedCleanupContext!.ActualTarget);
        h.Backend.FailStop = false;
        await c.StopAsync();
        Assert.Null(c.RetainedCleanupContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleOrForeignSnapshotAndRejectedStopNeverProveRelease(bool foreign)
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var result = await c.StartAsync(c.Confirm(h.Plan()));
        var expected = result.RetainedCleanupContext!;
        if (foreign)
            await h.Lifecycle.SwitchAsync(h.Plan(new DisplayCaptureTarget("display", "Display", 1)).Request);
        else
            await h.Lifecycle.HandleSourceLostAsync(new(expected.SessionId, expected.Generation, expected.ActualTarget, DateTimeOffset.UnixEpoch));
        var stops = h.Backend.Stops.Count;
        var stop = await c.StopAsync();
        Assert.Equal(CaptureFailureKind.Superseded, stop.Failure!.Kind);
        Assert.Same(expected, c.RetainedCleanupContext);
        Assert.Equal(stops, h.Backend.Stops.Count);
        Assert.False((await c.StartAsync(c.Confirm(h.Plan()))).StartTransition.Succeeded);
    }

    [Fact]
    public async Task FailedStopWithExplicitReleaseDoesNotRetainCleanup()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        await c.StartAsync(c.Confirm(h.Plan()));
        h.Backend.FailStop = true;
        h.Backend.CleanupRequired = false;
        var stop = await c.StopAsync();
        Assert.Equal(CaptureCommandDisposition.Failed, stop.Disposition);
        Assert.Same(stop, c.LastStopTransition);
        Assert.Null(c.RetainedCleanupContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingLossCorrelatesReturnedSessionBeforeRevoking(bool stale)
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        h.Backend.OnStart = (context, _) =>
        {
            h.Bus.Publish(new CaptureSourceLostEvent(stale ? context with { SessionId = Guid.NewGuid(), Generation = context.Generation + 1 } : context, "fixture"));
            return Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget));
        };
        var result = await c.StartAsync(c.Confirm(h.Plan()));
        await h.Manager.AwaitLastSourceLossHandlingAsync();
        Assert.True(result.StartTransition.Succeeded);
        Assert.Equal(!stale, result.Revoked);
        Assert.Equal(!stale, result.ReconciliationStopTransition is not null);
        Assert.Equal(stale ? 0 : 1, h.Backend.Stops.Count);
        Assert.Equal(stale, c.RetainedCleanupContext is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLossUsesExactTutorialStopAndPreservesFailedCleanup(bool failStop)
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var result = await c.StartAsync(c.Confirm(h.Plan()));
        h.Backend.FailStop = failStop;
        h.Bus.Publish(new CaptureSourceLostEvent(result.RetainedCleanupContext!, "fixture"));
        await h.Manager.AwaitLastSourceLossHandlingAsync();
        Assert.Equal(CaptureOperationKind.Stop, c.LastStopTransition!.Operation);
        Assert.Equal(!failStop, c.LastStopTransition.Succeeded);
        Assert.Equal(failStop, c.RetainedCleanupContext is not null);
        Assert.Single(h.Backend.Starts);
    }

    [Fact]
    public async Task HealthCheckRoutesTutorialDisplayLossThroughScopedStop()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        await c.StartAsync(c.Confirm(h.Plan(new DisplayCaptureTarget("display", "Display", 1))));
        h.Engine.Available = false;
        await h.Manager.CheckActiveSourceHealthAsync();
        Assert.True(c.LastStopTransition!.Succeeded);
        Assert.Null(c.RetainedCleanupContext);
        Assert.Single(h.Backend.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAndDisposeObserveLateStartAndTerminalReceipt(bool dispose)
    {
        await using var h = new Harness();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnStart = async (context, _) =>
        { entered.TrySetResult(true); await release.Task; return CaptureBackendStartResult.Success(context.RequestedTarget); };
        var c = h.Manager.TutorialCapture;
        var start = c.StartAsync(c.Confirm(h.Plan()));
        Task shutdown;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) { h.Manager.Dispose(); shutdown = h.Manager.AwaitDisposalAsync(); }
            else shutdown = h.Manager.StopAsync(CancellationToken.None);
            Assert.True(h.Lifecycle.IsAdmissionClosed);
            Assert.False(shutdown.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var result = await start;
        await shutdown;
        Assert.True(result.StartTransition.Succeeded);
        Assert.True(result.Revoked);
        Assert.NotNull(result.ReconciliationStopTransition);
        Assert.Equal(CaptureLifecycleStatus.Shutdown, h.Lifecycle.Snapshot.Status);
        Assert.Null(c.RetainedCleanupContext);
        Assert.Single(h.Backend.Stops);
    }

    [Fact]
    public async Task LossDuringCallerCancelledStopGetsSeparateUncancelledCleanupReceipt()
    {
        await using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var entered = Gate<bool>();
        var release = Gate<bool>();
        var c = h.Manager.TutorialCapture;
        var start = await c.StartAsync(c.Confirm(h.Plan()));
        var attempts = 0;
        h.Backend.OnStop = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) > 1) return CaptureBackendCommandResult.Success();
            entered.TrySetResult(true);
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            return CaptureBackendCommandResult.Failed(new(CaptureFailureKind.Cancelled, "fixture.cancelled", "Synthetic caller cancellation."));
        };
        var stop = c.StopAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            h.Bus.Publish(new CaptureSourceLostEvent(start.RetainedCleanupContext!, "fixture"));
            cancellation.Cancel();
        }
        finally { release.TrySetResult(true); }
        var result = await stop;
        await h.Manager.AwaitLastSourceLossHandlingAsync();
        Assert.Equal(CaptureFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Same(result, c.LastStopTransition);
        Assert.True(c.LastSourceLossStopTransition!.Succeeded);
        Assert.Equal(2, h.Backend.Stops.Count);
        Assert.False(h.Backend.StopTokens[1].CanBeCanceled);
        Assert.Null(c.RetainedCleanupContext);
    }

    [Fact]
    public async Task QueuedStartCancellationDoesNotClaimOrStopTheExistingOwner()
    {
        await using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var old = await h.Lifecycle.StartAsync(h.Plan().Request);
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnSave = async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendSaveResult.Failed(new(CaptureFailureKind.Unsupported, "fixture.save", "No media is produced."));
        };
        var save = h.Lifecycle.SaveAsync();
        var c = h.Manager.TutorialCapture;
        TutorialStartResult result;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var start = c.StartAsync(c.Confirm(h.Plan()), cancellation.Token);
            cancellation.Cancel();
            result = await start.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(true); }
        await save;
        Assert.Equal(CaptureFailureKind.Cancelled, result.StartTransition.Failure!.Kind);
        Assert.True(result.Revoked);
        Assert.Null(result.RetainedCleanupContext);
        Assert.Null(result.ReconciliationStopTransition);
        Assert.Equal(old.After.Context, h.Lifecycle.Snapshot.Context);
        Assert.Single(h.Backend.Starts);
        Assert.Empty(h.Backend.Stops);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("unavailable")]
    [InlineData("failed-replacement")]
    public async Task ReusingExactRequestCannotClaimPreexistingOwnerFromNonAcquisitionReceipt(string rejection)
    {
        await using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var plan = h.Plan();
        var old = await h.Manager.StartManualCaptureAsync(plan.Request);
        var c = h.Manager.TutorialCapture;
        if (rejection == "cancelled") cancellation.Cancel();
        if (rejection == "unavailable") h.Engine.Available = false;
        if (rejection == "failed-replacement") h.Backend.FailStop = true;
        var result = await c.StartAsync(c.Confirm(plan), cancellation.Token);
        Assert.False(result.StartTransition.Succeeded);
        Assert.Null(result.RetainedCleanupContext);
        Assert.Null(result.ReconciliationStopTransition);
        Assert.Equal(old.After.Context, h.Lifecycle.Snapshot.Context);
        Assert.Single(h.Backend.Starts);
        Assert.Equal(rejection == "failed-replacement" ? 1 : 0, h.Backend.Stops.Count);
    }

    [Fact]
    public async Task PendingSameRequestLossForOldSessionReturnsToManagersExactLossRoute()
    {
        await using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var plan = h.Plan();
        var old = await h.Manager.StartManualCaptureAsync(plan.Request);
        var entered = Gate<bool>();
        var release = Gate<bool>();
        h.Backend.OnSave = async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return CaptureBackendSaveResult.Failed(new(CaptureFailureKind.Unsupported, "fixture.save", "No media is produced."));
        };
        var save = h.Lifecycle.SaveAsync();
        var c = h.Manager.TutorialCapture;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var start = c.StartAsync(c.Confirm(plan), cancellation.Token);
            h.Bus.Publish(new CaptureSourceLostEvent(old.After.Context!, "fixture"));
            cancellation.Cancel();
            var result = await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result.RetainedCleanupContext);
            Assert.Null(result.ReconciliationStopTransition);
        }
        finally { release.TrySetResult(true); }
        await save;
        await h.Manager.AwaitLastSourceLossHandlingAsync();
        Assert.Single(h.Backend.Stops);
        Assert.Equal(old.After.Context, h.Backend.Stops[0]);
        Assert.Null(h.Lifecycle.Snapshot.Context);
        Assert.Null(c.LastStopTransition);
    }

    [Fact]
    public async Task PreCancelledStopRetainsExactCleanupUntilExplicitRetry()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var start = await c.StartAsync(c.Confirm(h.Plan()));
        var cancelled = await c.StopAsync(new CancellationToken(true));
        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure!.Kind);
        Assert.Same(cancelled, c.LastStopTransition);
        Assert.Equal(start.RetainedCleanupContext, c.RetainedCleanupContext);
        Assert.Empty(h.Backend.Stops);
        Assert.True((await c.StopAsync()).Succeeded);
    }

    [Fact]
    public async Task FailedTerminalCleanupRemainsRetainedUntilManagerRetriesShutdown()
    {
        await using var h = new Harness();
        var c = h.Manager.TutorialCapture;
        var start = await c.StartAsync(c.Confirm(h.Plan()));
        h.Backend.FailStop = true;
        await h.Manager.StopAsync(CancellationToken.None);
        Assert.Same(start.RetainedCleanupContext, c.RetainedCleanupContext);
        Assert.Equal(CaptureLifecycleStatus.Faulted, h.Lifecycle.Snapshot.Status);
        h.Backend.FailStop = false;
        await h.Manager.StopAsync(CancellationToken.None);
        Assert.Null(c.RetainedCleanupContext);
        Assert.Equal(CaptureLifecycleStatus.Shutdown, h.Lifecycle.Snapshot.Status);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public AppSettings Defaults { get; } = new()
        {
            DesktopCaptureEnabled = true,
            Capture = new() { Encoder = EncoderType.X264, MonitorStableId = "display", MonitorIndex = 1 },
            Storage = new() { SavePath = @"C:\SyntheticTutorial" },
            Buffer = new() { OutputDirectory = @"C:\SyntheticTutorial" },
            Webcam = new() { EnabledForExplicitManualRecording = true, DeviceId = "saved-private-camera" },
            GameProfiles = [new() { GameExecutableName = "game.exe" }],
        };
        public Engine Engine { get; } = new();
        public Backend Backend { get; } = new();
        public Detector Detector { get; } = new();
        public InMemoryEventBus Bus { get; } = new();
        public Settings Settings { get; } = new();
        public CaptureLifecycleCoordinator Lifecycle { get; }
        public CaptureSourceManager Manager { get; }
        public CaptureStartupBaselineResult Baseline { get; }
        public Harness()
        {
            Baseline = CaptureStartupBaselineResult.Create(Defaults);
            Backend.Baseline = Baseline;
            Lifecycle = new(Backend);
            Manager = new(Detector, Engine, Lifecycle, Bus, Defaults, Settings,
                new HighlightDetectorRegistry([], Bus, NullLogger<HighlightDetectorRegistry>.Instance),
                NullLogger<CaptureSourceManager>.Instance);
        }
        public TutorialRecordingPlan Plan(CaptureTarget? target = null, WebcamCaptureSelection? camera = null) =>
            TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
                target ?? new WindowCaptureTarget(42, new IntPtr(123), "Tutorial", "tutorial.exe", "Fixture"),
                Baseline, camera, camera is not null).Plan!;
        public async ValueTask DisposeAsync()
        {
            Backend.FailStop = false;
            await Manager.StopAsync(CancellationToken.None);
            Manager.Dispose();
            await Lifecycle.DisposeAsync();
        }
    }

    private sealed class Backend : ICaptureLifecycleBackend
    {
        public CaptureStartupBaselineResult Baseline { get; set; } = null!;
        public List<CaptureSessionContext> Starts { get; } = [];
        public List<CaptureSessionContext> Stops { get; } = [];
        public List<CancellationToken> StopTokens { get; } = [];
        public bool FailStop { get; set; }
        public bool CleanupRequired { get; set; } = true;
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendStartResult>>? OnStart { get; set; }
        public Func<Task<CaptureBackendSaveResult>>? OnSave { get; set; }
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendCommandResult>>? OnStop { get; set; }
        public CaptureFailure? ValidateStart(CaptureRequest request) => Baseline.Validate(request);
        public Task<CaptureBackendStartResult> StartAsync(CaptureSessionContext context, CancellationToken cancellationToken)
        {
            Starts.Add(context);
            return OnStart?.Invoke(context, cancellationToken) ?? Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget));
        }
        public Task<CaptureBackendCommandResult> StopAsync(CaptureSessionContext context, CancellationToken cancellationToken)
        {
            Stops.Add(context);
            StopTokens.Add(cancellationToken);
            if (OnStop is not null) return OnStop(context, cancellationToken);
            return Task.FromResult(FailStop
                ? CaptureBackendCommandResult.Failed(new(CaptureFailureKind.Backend, "fixture.stop", "Synthetic stop failed."), CleanupRequired)
                : CaptureBackendCommandResult.Success());
        }
        public Task<CaptureBackendSaveResult> SaveAsync(CaptureSessionContext context, CancellationToken cancellationToken) =>
            OnSave?.Invoke() ?? throw new InvalidOperationException("No media operations in this fixture.");
    }

#pragma warning disable CS0067
    private sealed class Settings : ISettingsService
    {
        public int Loads { get; private set; }
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() { Loads++; throw new InvalidOperationException("No settings reload."); }
        public void Save(AppSettings settings) => throw new InvalidOperationException("No settings writes.");
    }
    private sealed class Detector : IGameDetector
    {
        public GameInfo? CurrentGame { get; private set; }
        public event Action<GameInfo>? GameStarted;
        public event Action<GameInfo>? GameStopped;
        public void Emit(GameInfo game) { CurrentGame = game; GameStarted?.Invoke(game); }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class Engine : ICaptureEngine
    {
        public bool Available { get; set; } = true;
        public int Revalidations { get; private set; }
        public int RegionResolutions { get; private set; }
        public Action? BeforeRevalidation { get; set; }
        public Action? BeforeGameResolution { get; set; }
        public CaptureState State => CaptureState.Idle;
        public CaptureConfig Config { get; } = new();
        public bool IsDesktopCapture => false;
        public bool IsReplayBufferActive => true;
        public GameInfo? CurrentGame => null;
        public event Action<CaptureState>? StateChanged;
        public event Action<string>? ClipSaved;
        public event Action<string>? Error;
        public void EnsureInitialized() => throw new InvalidOperationException("No native initialization.");
        public DisplayCaptureTarget? ResolveDisplayTarget(int monitorIndex) => throw new InvalidOperationException("No index fallback.");
        public DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId) => Available ? new(stableDisplayId, "Display", 1) : null;
        public DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName) => throw new InvalidOperationException("No name fallback.");
        public bool IsDisplayTargetAvailable(DisplayCaptureTarget target) { Revalidations++; BeforeRevalidation?.Invoke(); return Available; }
        public RegionCaptureTarget? ResolveRegionTarget(string owningDisplayId, PhysicalCaptureBounds physicalBounds)
        { RegionResolutions++; throw new InvalidOperationException("No region reconstruction."); }
        public bool IsRegionTargetAvailable(RegionCaptureTarget target) { Revalidations++; BeforeRevalidation?.Invoke(); return Available; }
        public bool IsWindowTargetAvailable(WindowCaptureTarget target) { Revalidations++; BeforeRevalidation?.Invoke(); return Available; }
        public WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game)
        { BeforeGameResolution?.Invoke(); return new(game.ProcessId, new IntPtr(321), "Game", game.ExecutableName); }
        public Task StartDesktopCaptureAsync(DisplayCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task StartRegionCaptureAsync(RegionCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task StartWindowCaptureAsync(CaptureSource source, bool startReplayBuffer, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task StopAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<string?> SaveReplayAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public void Dispose() { }
    }
#pragma warning restore CS0067
}
