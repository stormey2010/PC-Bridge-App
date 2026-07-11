using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcBridge.Agent.Core;

public interface ISensorProvider : IAsyncDisposable
{
    string Name { get; }
    TimeSpan Interval { get; }
    IReadOnlyList<EntityDescriptor> Describe();
    Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken);
}

public interface ICommandHandler
{
    IReadOnlySet<string> Commands { get; }
    Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken);
}

public enum ConnectionStatus { Disconnected, Connecting, Connected, AuthenticationFailed, Incompatible }

public sealed record ConnectionSnapshot(ConnectionStatus Status, DateTimeOffset? LastConnected, string? FriendlyError = null);

public interface IConnectionStatusStore
{
    ConnectionSnapshot Current { get; }
    event EventHandler<ConnectionSnapshot>? Changed;
    void Set(ConnectionSnapshot snapshot);
}

public sealed class ConnectionStatusStore : IConnectionStatusStore
{
    private ConnectionSnapshot _current = new(ConnectionStatus.Disconnected, null);
    public ConnectionSnapshot Current => _current;
    public event EventHandler<ConnectionSnapshot>? Changed;
    public void Set(ConnectionSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(this, snapshot);
    }
}

/// <summary>Persists connection status so the WPF app can show live Home Assistant state.</summary>
public sealed class FileConnectionStatusStore(string directory) : IConnectionStatusStore
{
    private readonly string _path = Path.Combine(directory, "status.json");
    private readonly object _gate = new();
    private ConnectionSnapshot _current = new(ConnectionStatus.Disconnected, null);

    public string StatusPath => _path;
    public ConnectionSnapshot Current { get { lock (_gate) return _current; } }
    public event EventHandler<ConnectionSnapshot>? Changed;

    public void Set(ConnectionSnapshot snapshot)
    {
        lock (_gate) _current = snapshot;
        try
        {
            Directory.CreateDirectory(directory);
            var payload = new StatusFile(snapshot.Status.ToString(), snapshot.LastConnected, snapshot.FriendlyError, DateTimeOffset.UtcNow);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, Protocol.Json));
            File.Move(temp, _path, true);
        }
        catch
        {
            // Status is best-effort; never fail the agent because the UI file could not be written.
        }
        Changed?.Invoke(this, snapshot);
    }

    public static ConnectionSnapshot? TryRead(string directory)
    {
        var path = Path.Combine(directory, "status.json");
        try
        {
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(path), Protocol.Json);
            if (file is null || !Enum.TryParse<ConnectionStatus>(file.Status, true, out var status)) return null;
            return new ConnectionSnapshot(status, file.LastConnected, file.FriendlyError);
        }
        catch { return null; }
    }

    private sealed record StatusFile(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("last_connected")] DateTimeOffset? LastConnected,
        [property: JsonPropertyName("friendly_error")] string? FriendlyError,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
}
