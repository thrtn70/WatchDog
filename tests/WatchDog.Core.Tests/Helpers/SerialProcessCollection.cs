namespace WatchDog.Core.Tests.Helpers;

/// <summary>
/// Tests that start real child processes and wait for them to come up. xUnit runs this
/// collection after the parallel ones, so a cold powershell.exe start does not compete with
/// the rest of the suite for CPU on a CI runner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialProcessCollection
{
    public const string Name = "Serial process tests";
}
