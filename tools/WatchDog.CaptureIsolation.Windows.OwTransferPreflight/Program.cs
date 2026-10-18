using WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This preflight is Windows-only.");
    return 2;
}

try
{
    if (args is ["--verify-runtime-boundary-reducer"])
        return RuntimeBoundaryReducerChecks.Run();

    if (args is ["--verify-in-memory-supervision-driver"])
        return InMemorySupervisionDriverChecks.Run();

    if (args is ["--verify-diagnostic-envelope-codec"])
        return DiagnosticEnvelopeCodecChecks.Run();

    if (args is ["--verify-supervision-proposal"])
        return CollectorSupervisionProposalChecks.Run();

    if (args is ["--verify-collector-model"])
        return CollectorModelChecks.Run();

    if (args is ["--verify-inventory-contract"])
        return InventoryContractChecks.Run();

    if (args is ["--verify-early-contract"])
        return OwnerDeathWitness.RunEarlyContractChecks();

    if (args is ["--run", "--scenario", "owner-death"])
        return await OwnerDeathWitness.RunControllerAsync().ConfigureAwait(false);

    if (args is ["--run", "--scenario", var earlyName] &&
        ScenarioOptions.TryParse(earlyName, out var earlyScenario) && earlyScenario is { IsEarlyOwnerDeath: true })
        return await OwnerDeathWitness.RunEarlyControllerAsync(earlyScenario).ConfigureAwait(false);

    if (args is ["--owner-child", var earlyPipe, var earlyNonce, var earlyPid, var childScenarioName] &&
        int.TryParse(earlyPid, out var earlyControllerPid) && earlyControllerPid > 0 &&
        ScenarioOptions.TryParse(childScenarioName, out var childScenario) && childScenario is { IsEarlyOwnerDeath: true })
        return await OwnerRole.RunAsync(childScenario, earlyPipe, earlyNonce, earlyControllerPid).ConfigureAwait(false);

    if (args is ["--owner-child", var witnessPipe, var witnessNonce, var witnessPid] &&
        int.TryParse(witnessPid, out var controllerPid) && controllerPid > 0)
        return await OwnerRole.RunAsync(new("owner-death", "owner-death", null),
            witnessPipe, witnessNonce, controllerPid).ConfigureAwait(false);

    if (args is ["--run"])
        return await OwnerRole.RunAsync(ScenarioOptions.Success).ConfigureAwait(false);

    if (args is ["--run", "--scenario", var name] &&
        ScenarioOptions.TryParse(name, out var scenario) && scenario is not null)
        return await OwnerRole.RunAsync(scenario).ConfigureAwait(false);

    if (ChildOptions.TryParse(args, out var child) && child is not null)
        return await WriterRole.RunAsync(child).ConfigureAwait(false);

    Console.Error.WriteLine(
        "Usage: WatchDog.CaptureIsolation.Windows.OwTransferPreflight.exe " +
        "--run | --verify-early-contract | --verify-inventory-contract | --verify-collector-model | --verify-supervision-proposal | --verify-diagnostic-envelope-codec | --verify-in-memory-supervision-driver | --verify-runtime-boundary-reducer");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"preflight failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
