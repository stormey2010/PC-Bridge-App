using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcBridge.Agent.Core;

public static class Protocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 1_048_576;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string MessageType,
    string MessageId,
    DateTimeOffset Timestamp,
    string DeviceId,
    object? Payload = null);

public sealed record AgentRegistration(
    string InstallationId,
    string DeviceName,
    string AgentVersion,
    string Manufacturer,
    string Model,
    string WindowsVersion,
    IReadOnlyList<EntityDescriptor> Entities);

public sealed record EntityDescriptor(
    string Key,
    string Platform,
    string Name,
    string? DeviceClass = null,
    string? Unit = null,
    bool EnabledByDefault = true,
    string? EntityCategory = null);

public sealed record EntityState(string Key, object? Value, IReadOnlyDictionary<string, object?>? Attributes = null);
public sealed record StateBatch(IReadOnlyList<EntityState> States);
public sealed record CommandRequest(string Command, IReadOnlyDictionary<string, JsonElement>? Parameters = null);
public sealed record CommandResult(bool Success, string? ErrorCode, string Message, object? Data = null);

public sealed class DuplicateCommandGuard(TimeSpan retention)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();

    public bool TryAccept(string messageId, DateTimeOffset now)
    {
        foreach (var item in _seen.Where(item => now - item.Value > retention))
            _seen.TryRemove(item.Key, out _);
        return _seen.TryAdd(messageId, now);
    }
}
