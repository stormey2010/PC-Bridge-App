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
