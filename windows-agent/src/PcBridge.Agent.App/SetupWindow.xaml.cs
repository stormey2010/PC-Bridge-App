using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.App;

public partial class SetupWindow : Window
{
    private readonly SettingsStore _settingsStore; private readonly ICredentialStore _credentials; private readonly AgentSettings _settings; private bool _validated;
    public SetupWindow(SettingsStore settingsStore, ICredentialStore credentials, AgentSettings settings) { InitializeComponent(); _settingsStore = settingsStore; _credentials = credentials; _settings = settings; UrlBox.Text = settings.HomeAssistantUrl; DeviceBox.Text = settings.DeviceName; PrivacyBox.IsChecked = settings.PrivacySensorsEnabled; }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _validated = false; SaveButton.IsEnabled = false; StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(165,163,181)); StatusText.Text = "Testing encrypted connection…";
        try
        {
            if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Enter a valid http:// or https:// Home Assistant URL.");
            if (string.IsNullOrWhiteSpace(TokenBox.Password)) throw new InvalidOperationException("Enter a long-lived access token.");
            var wsUri = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket", Query = string.Empty }.Uri;
            using var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await socket.ConnectAsync(wsUri, timeout.Token);
            var buffer = new byte[4096]; await socket.ReceiveAsync(buffer, timeout.Token);
            var auth = JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", access_token = TokenBox.Password }); await socket.SendAsync(auth, WebSocketMessageType.Text, true, timeout.Token);
            var result = await socket.ReceiveAsync(buffer, timeout.Token); using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.Count));
            if (doc.RootElement.GetProperty("type").GetString() != "auth_ok") throw new UnauthorizedAccessException();
            _validated = true; SaveButton.IsEnabled = true; StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(115,226,185)); StatusText.Text = "Connected successfully. Your credential is valid.";
        }
        catch (UnauthorizedAccessException) { StatusText.Text = "PC Bridge could not authenticate. Check that the token is complete and has not been revoked."; }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException ? ex.Message : "PC Bridge could not reach Home Assistant. Check the URL, TLS certificate, and network connection."; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_validated) return; _settings.HomeAssistantUrl = UrlBox.Text.Trim().TrimEnd('/'); _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceBox.Text) ? Environment.MachineName : DeviceBox.Text.Trim(); _settings.PrivacySensorsEnabled = PrivacyBox.IsChecked == true;
        await _credentials.SaveTokenAsync(TokenBox.Password); TokenBox.Clear(); await _settingsStore.SaveAsync(_settings); DialogResult = true;
    }
}
