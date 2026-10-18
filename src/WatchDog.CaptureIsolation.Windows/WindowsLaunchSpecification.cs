using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Internal, test-injected launch material. Path and digest equality bind the requested plan but
/// are not publisher authenticity or package-verification evidence.
/// </summary>
internal sealed class WindowsLaunchSpecification
{
    internal WindowsLaunchSpecification(
        HelperLaunchRequest request,
        string applicationPath,
        string commandLine,
        string? environmentBlock = null,
        IEnumerable<SafeHandle>? inheritedChildHandles = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(applicationPath) ||
            !Path.IsPathFullyQualified(applicationPath) ||
            applicationPath.Contains('\0'))
        {
            throw new ArgumentException("An absolute application path is required.", nameof(applicationPath));
        }
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Contains('\0'))
            throw new ArgumentException("A mutable command line value is required.", nameof(commandLine));
        if (environmentBlock is not null && !environmentBlock.EndsWith("\0\0", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An explicit environment block must be double-null terminated.",
                nameof(environmentBlock));
        }

        ApplicationPath = applicationPath;
        CommandLine = commandLine;
        EnvironmentBlock = environmentBlock;
        InheritedChildHandles = Array.AsReadOnly(
            inheritedChildHandles?.ToArray() ?? []);
    }

    internal HelperLaunchRequest Request { get; }
    internal string ApplicationPath { get; }
    internal string CommandLine { get; }
    internal string? EnvironmentBlock { get; }
    internal ReadOnlyCollection<SafeHandle> InheritedChildHandles { get; }
}
