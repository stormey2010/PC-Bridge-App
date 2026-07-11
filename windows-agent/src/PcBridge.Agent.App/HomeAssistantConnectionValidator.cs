using System.Net.WebSockets;
using System.Text.Json;
using System.IO;

namespace PcBridge.Agent.App;

internal static class HomeAssistantConnectionValidator
{
    public static async Task ValidateAsync(string url, string token, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Enter a valid Home Assistant URL beginning with http:// or https://.");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Enter a long-lived access token, or keep the existing saved credential.");

        var wsUri = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
            Path = "/api/websocket",
            Query = string.Empty
        }.Uri;
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await socket.ConnectAsync(wsUri, timeout.Token);
        using var required = await ReceiveAsync(socket, timeout.Token);
        if (required.RootElement.GetProperty("type").GetString() != "auth_required")
            throw new InvalidOperationException("The server did not respond like Home Assistant.");
        var auth = JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", access_token = token });
        await socket.SendAsync(auth, WebSocketMessageType.Text, true, timeout.Token);
        using var result = await ReceiveAsync(socket, timeout.Token);
        if (result.RootElement.GetProperty("type").GetString() != "auth_ok")
            throw new UnauthorizedAccessException("Home Assistant rejected the saved credential.");

        // Probe for the PC Bridge integration without registering a live agent session.
        var probe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = 1,
            type = "pc_bridge/register_agent",
            protocol_version = 1,
            registration = new { }
        });
        await socket.SendAsync(probe, WebSocketMessageType.Text, true, timeout.Token);
        using var probeResult = await ReceiveResultAsync(socket, 1, timeout.Token);
        if (!probeResult.RootElement.TryGetProperty("success", out var success) || success.GetBoolean())
            return;
        var code = probeResult.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString()
            : null;
        if (string.Equals(code, "unknown_command", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Home Assistant accepted the credential, but the PC Bridge integration is not installed or not loaded. Install PC Bridge in Home Assistant, restart HA, then try again.");
        if (string.Equals(code, "incompatible_version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Home Assistant has PC Bridge installed, but the protocol version does not match this agent.");
        // invalid_format / message_too_large means the integration is present — connection is good.
    }

    private static async Task<JsonDocument> ReceiveResultAsync(ClientWebSocket socket, int requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveAsync(socket, cancellationToken);
            var root = message.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "event")
            {
                message.Dispose();
                continue;
            }
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == requestId)
                return message;
            message.Dispose();
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Home Assistant closed the connection.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 65_536) throw new InvalidOperationException("Home Assistant returned an oversized response.");
        } while (!result.EndOfMessage);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
