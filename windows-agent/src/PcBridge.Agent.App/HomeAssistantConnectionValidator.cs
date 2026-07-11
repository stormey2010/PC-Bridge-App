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
