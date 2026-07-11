using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Service;

public sealed class HomeAssistantConnection(ILogger<HomeAssistantConnection> logger, IConnectionStatusStore statusStore) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private int _requestId;
    public event Func<string, CommandRequest, Task>? CommandReceived;

    public async Task ConnectAsync(Uri baseUri, string token, AgentRegistration registration, CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        statusStore.Set(new(ConnectionStatus.Connecting, statusStore.Current.LastConnected));
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket", Query = string.Empty };
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(builder.Uri, cancellationToken);
        var required = await ReceiveJsonAsync(cancellationToken);
        if (required.RootElement.GetProperty("type").GetString() != "auth_required") throw new ProtocolException("Home Assistant did not request authentication.");
        await SendRawAsync(new { type = "auth", access_token = token }, cancellationToken);
        using var auth = await ReceiveJsonAsync(cancellationToken);
        var authType = auth.RootElement.GetProperty("type").GetString();
        if (authType == "auth_invalid")
        {
            statusStore.Set(new(ConnectionStatus.AuthenticationFailed, statusStore.Current.LastConnected, "PC Bridge could not authenticate with Home Assistant. The saved credential may have expired or been revoked."));
            throw new AuthenticationException();
        }
        if (authType != "auth_ok") throw new ProtocolException("Unexpected authentication response.");

        var registrationId = await SendCommandAsync("pc_bridge/register_agent", new { protocol_version = Protocol.Version, registration }, cancellationToken);
        using var registrationResult = await ReceiveJsonAsync(cancellationToken);
        var registrationRoot = registrationResult.RootElement;
        if (!registrationRoot.TryGetProperty("id", out var responseId) || responseId.GetInt32() != registrationId ||
            registrationRoot.GetProperty("type").GetString() != "result" || !registrationRoot.GetProperty("success").GetBoolean())
        {
            statusStore.Set(new(ConnectionStatus.Incompatible, statusStore.Current.LastConnected, "This PC Bridge Agent version is not compatible with the installed Home Assistant integration."));
            throw new ProtocolException("Home Assistant rejected agent registration.");
        }
        statusStore.Set(new(ConnectionStatus.Connected, DateTimeOffset.UtcNow));
        logger.LogInformation("Connected to Home Assistant as installation {InstallationId}", registration.InstallationId);
    }

    public async Task SendStatesAsync(string deviceId, IReadOnlyList<EntityState> states, CancellationToken cancellationToken) =>
        _ = await SendCommandAsync("pc_bridge/state_update", new { device_id = deviceId, message_id = Guid.NewGuid().ToString("N"), timestamp = DateTimeOffset.UtcNow, states }, cancellationToken);

    public async Task SendCommandResultAsync(string deviceId, string messageId, CommandResult result, CancellationToken cancellationToken) =>
        _ = await SendCommandAsync("pc_bridge/command_result", new { device_id = deviceId, message_id = messageId, result }, cancellationToken);

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (_socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveJsonAsync(cancellationToken);
            var root = message.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "event" && root.TryGetProperty("event", out var eventPayload) &&
                eventPayload.TryGetProperty("message_type", out var messageType) && messageType.GetString() == "command")
            {
                var messageId = eventPayload.GetProperty("message_id").GetString() ?? string.Empty;
                var command = eventPayload.GetProperty("command").Deserialize<CommandRequest>(Protocol.Json);
                if (command is not null && CommandReceived is not null) await CommandReceived(messageId, command);
            }
        }
    }

    private async Task<int> SendCommandAsync(string type, object payload, CancellationToken cancellationToken)
    {
        var document = JsonSerializer.SerializeToElement(payload, Protocol.Json);
        var id = Interlocked.Increment(ref _requestId);
        var values = new Dictionary<string, object?> { ["id"] = id, ["type"] = type };
        foreach (var property in document.EnumerateObject()) values[property.Name] = property.Value.Clone();
        await SendRawAsync(values, cancellationToken);
        return id;
    }

    private async Task SendRawAsync(object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        if (bytes.Length > Protocol.MaximumMessageBytes) throw new ProtocolException("Message exceeds the configured size limit.");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket?.State != WebSocketState.Open) throw new WebSocketException("The Home Assistant connection is not open.");
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        if (_socket is null) throw new WebSocketException("Socket is not initialized.");
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Home Assistant closed the connection.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > Protocol.MaximumMessageBytes) throw new ProtocolException("Received message exceeds the configured size limit.");
        } while (!result.EndOfMessage);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket?.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent stopping", CancellationToken.None);
        _socket?.Dispose(); _sendLock.Dispose();
    }
}

public sealed class ProtocolException(string message) : Exception(message);
public sealed class AuthenticationException : Exception;
