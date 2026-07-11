using System.Windows;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.App;

public partial class SetupWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentials;
    private readonly AgentSettings _settings;
    private bool _validated;
    private string? _validatedToken;

    public SetupWindow(SettingsStore settingsStore, ICredentialStore credentials, AgentSettings settings)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _credentials = credentials;
        _settings = settings;
        UrlBox.Text = settings.HomeAssistantUrl;
        DeviceBox.Text = settings.DeviceName;
        PrivacyBox.IsChecked = settings.PrivacySensorsEnabled;
        Title = string.IsNullOrWhiteSpace(settings.HomeAssistantUrl) ? "Set up PC Bridge Agent" : "Edit PC Bridge connection";
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _validated = false; SaveButton.IsEnabled = false; StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(165,163,181)); StatusText.Text = "Testing encrypted connection…";
        try
        {
            _validatedToken = string.IsNullOrWhiteSpace(TokenBox.Password)
                ? await _credentials.GetTokenAsync()
                : TokenBox.Password;
            await HomeAssistantConnectionValidator.ValidateAsync(UrlBox.Text, _validatedToken ?? string.Empty);
            _validated = true; SaveButton.IsEnabled = true; StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(115,226,185)); StatusText.Text = "Connected successfully. Your credential is valid.";
        }
        catch (UnauthorizedAccessException) { StatusText.Text = "PC Bridge could not authenticate. Check that the token is complete and has not been revoked."; }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException ? ex.Message : "PC Bridge could not reach Home Assistant. Check the URL, TLS certificate, and network connection."; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_validated || string.IsNullOrWhiteSpace(_validatedToken)) return;
        _settings.HomeAssistantUrl = UrlBox.Text.Trim().TrimEnd('/');
        _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceBox.Text) ? Environment.MachineName : DeviceBox.Text.Trim();
        _settings.PrivacySensorsEnabled = PrivacyBox.IsChecked == true;
        await _credentials.SaveTokenAsync(_validatedToken);
        TokenBox.Clear();
        await _settingsStore.SaveAsync(_settings);
        DialogResult = true;
    }
}
