using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchDog.CaptureProtocol;

public static class BoundedJsonCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static byte[] Serialize<T>(T value, CaptureProtocolLimits limits)
        where T : IProtocolValidatable
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(limits);
        value.Validate(limits);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        Validate(bytes, limits);
        return bytes;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, CaptureProtocolLimits limits)
        where T : IProtocolValidatable
    {
        ArgumentNullException.ThrowIfNull(limits);
        Validate(bytes, limits);
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, SerializerOptions);
            if (value is null)
                throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
            value.Validate(limits);
            return value;
        }
        catch (CaptureProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.MalformedJson);
        }
        catch (NotSupportedException)
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }

    public static void Validate(ReadOnlySpan<byte> bytes, CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (bytes.Length > limits.MaxPayloadBytes)
            throw ProtocolErrors.Create(ProtocolErrorCode.PayloadTooLarge);

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                // Payload bytes are already bounded. A larger parser ceiling lets the protocol's
                // injected depth limit produce the deterministic rejection before deserialization.
                MaxDepth = 1024,
            });
            var containers = new Stack<ContainerState>();
            var nodes = 0;

            while (reader.Read())
            {
                if (reader.CurrentDepth > limits.MaxJsonDepth)
                    throw ProtocolErrors.Create(ProtocolErrorCode.JsonDepthExceeded);

                if (containers.TryPeek(out var parent) &&
                    parent.Kind == JsonTokenType.StartArray &&
                    reader.TokenType != JsonTokenType.EndArray &&
                    reader.CurrentDepth == containers.Count)
                {
                    parent.ItemCount++;
                    if (parent.ItemCount > limits.MaxCollectionItems)
                        throw ProtocolErrors.Create(ProtocolErrorCode.JsonCollectionLimitExceeded);
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        containers.Push(new ContainerState(JsonTokenType.StartObject));
                        break;
                    case JsonTokenType.StartArray:
                        containers.Push(new ContainerState(JsonTokenType.StartArray));
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0)
                            throw ProtocolErrors.Create(ProtocolErrorCode.MalformedJson);
                        containers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        {
                            if (!containers.TryPeek(out var current) ||
                                current.Kind != JsonTokenType.StartObject)
                            {
                                throw ProtocolErrors.Create(ProtocolErrorCode.MalformedJson);
                            }

                            current.ItemCount++;
                            if (current.ItemCount > limits.MaxObjectProperties)
                                throw ProtocolErrors.Create(ProtocolErrorCode.JsonObjectLimitExceeded);
                            var propertyName = reader.GetString() ?? string.Empty;
                            EnsureStringLimit(propertyName, limits);
                            if (!current.PropertyNames.Add(propertyName))
                                throw ProtocolErrors.Create(ProtocolErrorCode.DuplicateJsonField);
                            break;
                        }
                    case JsonTokenType.String:
                        EnsureStringLimit(reader.GetString() ?? string.Empty, limits);
                        break;
                }

                nodes++;
                if (nodes > limits.MaxTotalJsonNodes)
                    throw ProtocolErrors.Create(ProtocolErrorCode.JsonNodeLimitExceeded);
            }

            if (containers.Count != 0 || nodes == 0)
                throw ProtocolErrors.Create(ProtocolErrorCode.MalformedJson);
        }
        catch (CaptureProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.MalformedJson);
        }
    }

    private static void EnsureStringLimit(string value, CaptureProtocolLimits limits)
    {
        if (Encoding.UTF8.GetByteCount(value) > limits.MaxStringUtf8Bytes)
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonStringLimitExceeded);
    }

    private sealed class ContainerState(JsonTokenType kind)
    {
        public JsonTokenType Kind { get; } = kind;
        public int ItemCount { get; set; }
        public HashSet<string> PropertyNames { get; } = new(StringComparer.Ordinal);
    }
}
