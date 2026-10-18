using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class ProtocolConstants
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
}

internal sealed record ChildOptions(
    string PipeName,
    string Nonce,
    int ParentPid,
    string TrustedParentPath,
    string PrivateRootName,
    string Scenario)
{
    internal static bool TryParse(string[] args, out ChildOptions? options)
    {
        options = null;
        if (args.Length != 13 || args[0] != "--child")
            return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                return false;
            if (!values.TryAdd(args[index], args[index + 1]))
                return false;
        }

        if (!values.TryGetValue("--pipe", out var pipe) ||
            !values.TryGetValue("--nonce", out var nonce) ||
            !values.TryGetValue("--parent-pid", out var parentText) ||
            !values.TryGetValue("--trusted-parent", out var trustedParent) ||
            !values.TryGetValue("--private-root", out var privateRoot) ||
            !values.TryGetValue("--scenario", out var scenario) ||
            !ScenarioOptions.TryParse(scenario, out _) ||
            !int.TryParse(parentText, out var parentPid) || parentPid <= 0 ||
            string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(nonce) ||
            !Path.IsPathFullyQualified(trustedParent) || !NativeMethods.IsSingleComponent(privateRoot))
            return false;

        options = new(pipe, nonce, parentPid, trustedParent, privateRoot, scenario);
        return true;
    }
}

internal sealed record WireMessage(
    string Type,
    string Nonce,
    int Pid,
    string? Resource = null,
    long? Handle = null,
    string? Name = null,
    HandleObservation? Observation = null,
    IReadOnlyList<SourceOffer>? Sources = null,
    string? Detail = null,
    Guid? LeaseId = null,
    long? Generation = null);

internal sealed record SourceOffer(
    string Resource,
    long Handle,
    string Name,
    HandleObservation Observation,
    Guid LeaseId,
    long Generation);

internal sealed record KernelHandleObservation(uint GrantedAccess, bool Inheritable);

internal sealed record HandleObservation(
    string Resource,
    uint GrantedAccess,
    bool Inheritable,
    bool IsDirectory,
    bool IsReparsePoint,
    ulong VolumeSerialNumber,
    string FileId,
    uint NumberOfLinks,
    long EndOfFile,
    string FinalPath);

internal sealed class MessageChannel : IAsyncDisposable
{
    private const int MaximumFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stream _stream;

    internal MessageChannel(Stream stream) => _stream = stream;

    internal async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolConstants.MessageTimeout);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length is 0 or > MaximumFrameBytes)
            throw new InvalidDataException("The O/W message exceeds the bounded frame size.");
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await _stream.WriteAsync(prefix, timeout.Token).ConfigureAwait(false);
        await _stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    internal async Task<WireMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await _stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("The O/W peer supplied an invalid frame length.");
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WireMessage>(payload, JsonOptions) ??
            throw new InvalidDataException("The O/W channel returned an empty message.");
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class ProtocolChecks
{
    internal static void Require(WireMessage message, string type, string nonce, int pid)
    {
        if (!StringComparer.Ordinal.Equals(message.Type, type) ||
            !StringComparer.Ordinal.Equals(message.Nonce, nonce) || message.Pid != pid)
            throw new InvalidDataException($"Unexpected {message.Type} message or peer identity.");
    }
}
