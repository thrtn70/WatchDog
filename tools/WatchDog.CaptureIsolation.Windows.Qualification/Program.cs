using System.Diagnostics;
using WatchDog.CaptureIsolation.Windows.Qualification;

// B1 has no runtime route until the launcher, runner, and native authority are admitted.
if (FixtureB1EntryDispatch.Classify(args) != FixtureEntryRoute.Legacy)
    return (int)QualificationExitCode.UsageOrPrerequisite;

if (!QualificationOptions.TryParse(args, out var parsed) || parsed is null)
    return (int)QualificationExitCode.UsageOrPrerequisite;

var options = parsed;
if (!FixtureArtifactRootAdmission.IsAdmitted(options))
    return (int)QualificationExitCode.UsageOrPrerequisite;

var clock = Stopwatch.StartNew();
QualificationArtifacts artifacts;
try
{
    artifacts = new QualificationArtifacts(options, clock);
    artifacts.Log("role-start");
}
catch
{
    return (int)QualificationExitCode.InfrastructureExceptionCleanupConfirmed;
}

using var lifetime = new CancellationTokenSource(
    options.Role == QualificationRole.Runner
        ? QualificationConstants.RunTimeout
        : QualificationConstants.ChildEmergencyExpiry + TimeSpan.FromSeconds(5));

try
{
    var result = options.Role switch
    {
        QualificationRole.Runner => await QualificationRunner.RunAsync(
            options,
            artifacts,
            clock).ConfigureAwait(false),
        QualificationRole.Owner => await QualificationRoles.RunOwnerAsync(
            options,
            artifacts,
            lifetime.Token).ConfigureAwait(false),
        QualificationRole.Helper => await QualificationRoles.RunHelperAsync(
            options,
            artifacts,
            lifetime.Token).ConfigureAwait(false),
        QualificationRole.Descendant => await QualificationRoles.RunDescendantAsync(
            options,
            artifacts,
            lifetime.Token).ConfigureAwait(false),
        _ => QualificationExitCode.UsageOrPrerequisite,
    };
    TryLog(artifacts, "role-complete", $"exit-{(int)result}");
    return (int)result;
}
catch (QualificationCleanupException exception)
{
    TryLog(artifacts, "cleanup-incomplete", exception.Code);
    return (int)QualificationExitCode.CleanupIncomplete;
}
catch (QualificationTimeoutException exception)
{
    TryLog(artifacts, "timeout-cleanup-confirmed", exception.Code);
    return (int)QualificationExitCode.TimeoutCleanupConfirmed;
}
catch (QualificationAssertionException exception)
{
    TryLog(artifacts, "assertion-failed-cleanup-confirmed", exception.Code);
    return (int)QualificationExitCode.AssertionFailedCleanupConfirmed;
}
catch (OperationCanceledException)
{
    TryLog(artifacts, "timeout-cleanup-confirmed", "fixture.role.timeout");
    return (int)QualificationExitCode.TimeoutCleanupConfirmed;
}
catch (WatchDog.CaptureIsolation.HelperBoundaryException exception)
{
    TryLog(artifacts, "boundary-failed-cleanup-confirmed", exception.FailureCode);
    return (int)QualificationExitCode.AssertionFailedCleanupConfirmed;
}
catch (Exception)
{
    TryLog(artifacts, "infrastructure-failed-cleanup-confirmed", "fixture.infrastructure.failed");
    return (int)QualificationExitCode.InfrastructureExceptionCleanupConfirmed;
}

static void TryLog(QualificationArtifacts artifacts, string @event, string code)
{
    try
    {
        artifacts.Log(@event, code);
    }
    catch
    {
        // Exit-code stability must not depend on artifact logging after the role has completed.
    }
}
