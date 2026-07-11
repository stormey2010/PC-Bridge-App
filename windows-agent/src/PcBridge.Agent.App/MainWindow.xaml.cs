using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PcBridge.Agent.Core;
using PcBridge.Agent.Windows;

namespace PcBridge.Agent.App;

public partial class MainWindow : Window
{
    private const string ServiceName = "PC Bridge Agent";
    private readonly AgentSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentials;
    private readonly bool _demo;
    private readonly Dictionary<string, CheckBox> _sensorChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _controlChecks = new(StringComparer.OrdinalIgnoreCase);
    private CheckBox? _privacyCheck;
    private CheckBox? _startupCheck;

    public MainWindow(AgentSettings settings, SettingsStore settingsStore, ICredentialStore credentials, bool demo = false)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _demo = demo;
        PageContent.Content = BuildOverview();
        RefreshServiceStatus();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in Navigation.Children.OfType<Button>()) child.Tag = null;
        var button = (Button)sender;
        button.Tag = "selected";
        var title = button.Content.ToString()!.Split("   ").Last();
        PageTitle.Text = title;
        PageContent.Content = title switch
        {
            "Overview" => BuildOverview(),
            "Home Assistant" => BuildConnection(),
            "Sensors" => BuildSensorPage(),
            "Controls" => BuildControls(),
            "Applications" => BuildApplications(),
            "Commands" => BuildCommands(),
            "Logs" => BuildLogs(),
            "Settings" => BuildSettings(),
            _ => BuildComingSoon(title)
        };
    }

    private FrameworkElement BuildOverview()
    {
        var root = Stack();
        root.Children.Add(Heading(_settings.DeviceName, "Your Windows PC, connected without opening an inbound port."));
        var hero = Card();
        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = Stack();
        identity.Children.Add(Label("HOME ASSISTANT", 11, "#A5A3B5"));
        identity.Children.Add(Label(string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) ? "Not configured" : _settings.HomeAssistantUrl, 16));
        identity.Children.Add(Badge("●  Configuration managed locally", "#1E4A3E", "#73E2B9"));
        heroGrid.Children.Add(identity);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in new[] { "Lock", "Sleep", "Restart", "Shut down" }) actions.Children.Add(ActionButton(action));
        Grid.SetColumn(actions, 1);
        heroGrid.Children.Add(actions);
        hero.Child = heroGrid;
        root.Children.Add(hero);
        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(Metric("CPU", _demo ? "18%" : "Live in HA", "Updates every 5 sec", "#7568FF"));
        metrics.Children.Add(Metric("Memory", _demo ? "42%" : "Live in HA", "Event stream", "#41C7A5"));
        metrics.Children.Add(Metric("Volume", _demo ? "35%" : "Live in HA", "Default output", "#49A8FF"));
        metrics.Children.Add(Metric("Keep awake", "Configurable", "Controls page", "#F0A53A"));
        root.Children.Add(metrics);
        var lower = new Grid();
        lower.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var activity = Card();
        var activityContent = Stack();
        activityContent.Children.Add(Label("Get started", 16));
        activityContent.Children.Add(Body("Edit the Home Assistant connection, choose Sensors and Controls, then restart the agent when prompted."));
        activity.Child = activityContent;
        lower.Children.Add(activity);
        var security = Card();
        var securityContent = Stack();
        securityContent.Children.Add(Label("Secure by default", 16));
        securityContent.Children.Add(Body("Outbound TLS connection\nCredential protected by Windows DPAPI\nNo generic remote shell\nDestructive controls disabled locally"));
        security.Child = securityContent;
        Grid.SetColumn(security, 1);
        lower.Children.Add(security);
        root.Children.Add(lower);
        return root;
    }

    private FrameworkElement BuildConnection()
    {
        var actions = ActionBar(
            ("Edit connection", EditConnection_Click, true),
            ("Test saved connection", TestConnection_Click, false),
            ("Restart agent", RestartAgent_Click, false),
            ("Remove credential", RemoveCredential_Click, false));
        return Page("Home Assistant connection", "Edit and validate the URL, credential, and PC name here.",
            Row("Server", string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) ? "Not configured" : _settings.HomeAssistantUrl),
            Row("PC name", _settings.DeviceName),
            Row("Installation ID", _settings.InstallationId),
            Row("Protocol", "Version 1"),
            Row("Authentication", "Credential protected by Windows DPAPI"),
            actions);
    }

    private FrameworkElement BuildSensorPage()
    {
        _sensorChecks.Clear();
        var system = SensorToggle("system", "System", "CPU, memory, uptime, idle time, lock and power state");
        var audio = SensorToggle("audio", "Audio", "Volume, mute and default output device");
        var network = SensorToggle("network", "Network", "Local IP plus upload and download rate");
        var keepAwake = SensorToggle("keep_awake", "Keep awake", "Keep-awake state, reason and remaining time");
        return Page("Sensor settings", "Choose which sensor groups the background agent registers. Changes apply after an agent restart.",
            system, audio, network, keepAwake,
            Section("Hardware monitoring", "CPU/GPU temperature and fan speed", "Unavailable — no supported provider detected"),
            ActionBar(("Save sensor settings", SaveSensors_Click, true)));
    }

    private FrameworkElement BuildControls()
    {
        _controlChecks.Clear();
        var controls = new[]
        {
            ControlToggle("system.lock", "Lock", "Lock the current Windows session"),
            ControlToggle("system.sleep", "Sleep", "Put the PC into sleep mode"),
            ControlToggle("system.restart", "Restart", "Destructive — confirmation is shown locally"),
            ControlToggle("system.shutdown", "Shut down", "Destructive — confirmation is shown locally"),
            ControlToggle("audio.set_volume", "Volume control", "Allow Home Assistant to set volume"),
            ControlToggle("audio.set_mute", "Mute control", "Allow Home Assistant to mute or unmute"),
            ControlToggle("keep_awake.set", "Keep awake", "Allow Home Assistant to manage keep-awake")
        };
        return Page("Approved controls", "Disabled controls are rejected by the Windows agent even if Home Assistant sends them manually.",
            controls.Append(ActionBar(("Save control settings", SaveControls_Click, true))).ToArray());
    }

    private FrameworkElement BuildApplications() => Page("Application allowlist", "Application controls are not implemented in this release.",
        Empty("No buttons are shown because the Phase 1 agent cannot safely manage applications yet.", "Coming in Phase 2"));

    private FrameworkElement BuildCommands() => Page("Commands", "Arbitrary remote commands are not implemented in this release.",
        Badge("SHIELDED  No remote shell exposed", "#1E4A3E", "#73E2B9"),
        Empty("PC Bridge currently accepts only the fixed controls shown on the Controls page.", "Safe Phase 1 mode"));

    private FrameworkElement BuildLogs() => Page("Logs & diagnostics", "Open local rotating logs or export a redacted configuration snapshot.",
        Row("Log level", "Information"),
        Row("Retention", "14 rolling files"),
        ActionBar(("Open log folder", OpenLogs_Click, true), ("Export diagnostics", ExportDiagnostics_Click, false)));

    private FrameworkElement BuildSettings()
    {
        _startupCheck = Toggle("Start the Windows service automatically", "Configure the installed PC Bridge Agent service to start at boot", _settings.StartAutomatically);
        _privacyCheck = Toggle("Privacy-sensitive sensors", "Master permission for future activity, user, microphone and camera providers", _settings.PrivacySensorsEnabled);
        return Page("Settings", "These settings are saved locally on this PC.",
            _startupCheck,
            _privacyCheck,
            Section("Appearance", "Dark Fluent theme  •  Violet accent  •  Comfortable density", "Active"),
            ActionBar(("Save settings", SaveGeneralSettings_Click, true), ("Open Windows Services", OpenServices_Click, false)));
    }

    private FrameworkElement BuildComingSoon(string title) => Page(title, "This feature is not implemented in the current release.",
        Empty("The page is intentionally read-only so it does not imply an action succeeded.", "Coming later"));

    private CheckBox SensorToggle(string key, string title, string description)
    {
        var check = Toggle(title, description, _settings.EnabledSensorGroups.GetValueOrDefault(key, true));
        _sensorChecks[key] = check;
        return check;
    }

    private CheckBox ControlToggle(string command, string title, string description)
    {
        var check = Toggle(title, description, _settings.EnabledControls.GetValueOrDefault(command));
        _controlChecks[command] = check;
        return check;
    }

    private static CheckBox Toggle(string title, string description, bool enabled)
    {
        var content = Stack();
        content.Children.Add(Label(title, 15));
        var detail = Body(description);
        detail.Margin = new(0, 3, 0, 0);
        content.Children.Add(detail);
        return new CheckBox
        {
            IsChecked = enabled,
            Content = content,
            Foreground = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new(4),
            Margin = new(0, 2, 0, 6)
        };
    }

    private async void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SetupWindow(_settingsStore, _credentials, _settings) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            PageContent.Content = BuildConnection();
            if (MessageBox.Show("Connection saved. Restart the background agent now?", "Restart agent", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await RestartServiceElevatedAsync();
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = await _credentials.GetTokenAsync() ?? string.Empty;
            await HomeAssistantConnectionValidator.ValidateAsync(_settings.HomeAssistantUrl, token);
            MessageBox.Show("Home Assistant accepted the saved URL and credential.", "Connection successful", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Home Assistant rejected the saved credential. Select Edit connection and enter a new token.", "Authentication failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is InvalidOperationException ? ex.Message : "PC Bridge could not reach Home Assistant. Check the URL, certificate and network.", "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Remove the saved Home Assistant credential and disconnect this PC?", "Remove credential", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _credentials.RemoveTokenAsync();
        _settings.HomeAssistantUrl = string.Empty;
        await _settingsStore.SaveAsync(_settings);
        PageContent.Content = BuildConnection();
        MessageBox.Show("The credential was removed. Use Edit connection to pair again.", "Credential removed", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RestartAgent_Click(object sender, RoutedEventArgs e) => await RestartServiceElevatedAsync();

    private async void SaveSensors_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _sensorChecks) _settings.EnabledSensorGroups[item.Key] = item.Value.IsChecked == true;
        await SaveAndOfferRestartAsync("Sensor settings saved.");
    }

    private async void SaveControls_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _controlChecks) _settings.EnabledControls[item.Key] = item.Value.IsChecked == true;
        await SaveAndOfferRestartAsync("Control settings saved.");
    }

    private async void SaveGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        var autoStartChanged = _settings.StartAutomatically != (_startupCheck?.IsChecked == true);
        _settings.StartAutomatically = _startupCheck?.IsChecked == true;
        _settings.PrivacySensorsEnabled = _privacyCheck?.IsChecked == true;
        await _settingsStore.SaveAsync(_settings);
        if (autoStartChanged && !_demo)
        {
            try { await RunElevatedAsync("sc.exe", $"config \"{ServiceName}\" start= {(_settings.StartAutomatically ? "auto" : "demand")}"); }
            catch (Exception ex) { MessageBox.Show($"Settings were saved, but Windows service startup could not be changed: {ex.Message}", "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        MessageBox.Show("Settings saved.", "PC Bridge Agent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveAndOfferRestartAsync(string message)
    {
        await _settingsStore.SaveAsync(_settings);
        if (MessageBox.Show($"{message}\n\nRestart the background agent now to apply it?", "Settings saved", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RestartServiceElevatedAsync();
    }

    private async Task RestartServiceElevatedAsync()
    {
        if (_demo) { MessageBox.Show("Demo: agent service restarted.", "PC Bridge Agent"); return; }
        try
        {
            await RunElevatedAsync("cmd.exe", $"/c sc.exe stop \"{ServiceName}\" >nul 2>&1 & timeout /t 2 /nobreak >nul & sc.exe start \"{ServiceName}\"");
            RefreshServiceStatus();
            MessageBox.Show("The PC Bridge Agent service was restarted.", "Agent restarted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("Administrator approval was cancelled, so the agent was not restarted.", "Restart cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The service could not be restarted. Open Windows Services and restart PC Bridge Agent manually.\n\n{ex.Message}", "Restart failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task RunElevatedAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Windows could not start the requested service action.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows returned error code {process.ExitCode}.");
    }

    private void RefreshServiceStatus()
    {
        var running = _demo || IsServiceRunning();
        AgentStatusText.Text = running ? "Agent service running" : "Agent service stopped";
        AgentStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#44D19D" : "#F06D6D"));
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"query \"{ServiceName}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"pc-bridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "JSON diagnostics|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var diagnostics = new
        {
            generated_at = DateTimeOffset.UtcNow,
            agent_version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3),
            installation_id = _settings.InstallationId,
            home_assistant_configured = !string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl),
            service_running = IsServiceRunning(),
            sensor_groups = _settings.EnabledSensorGroups,
            enabled_controls = _settings.EnabledControls.Where(item => item.Value).Select(item => item.Key).ToArray(),
            privacy_sensors_enabled = _settings.PrivacySensorsEnabled,
            fast_update_seconds = _settings.FastUpdateSeconds
        };
        System.IO.File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show("Redacted diagnostics exported. No token, username, URL, window title or application name was included.", "Diagnostics exported", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenServices_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent", "logs");
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        var action = (string)((Button)sender).Tag;
        var command = action switch { "Lock" => "system.lock", "Sleep" => "system.sleep", "Restart" => "system.restart", _ => "system.shutdown" };
        if (!_settings.EnabledControls.GetValueOrDefault(command)) { MessageBox.Show($"{action} is disabled on the Controls page.", "Control disabled", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (action is "Restart" or "Shut down" && MessageBox.Show($"Are you sure you want to {action.ToLowerInvariant()} this PC?", "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_demo) { MessageBox.Show($"Demo: {action} command accepted.", "PC Bridge Agent"); return; }
        var result = await new PowerCommandHandler().ExecuteAsync(command, null, CancellationToken.None);
        if (!result.Success) MessageBox.Show(result.Message, "Command failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static StackPanel Stack() => new() { Margin = new Thickness(0) };
    private static Border Card() => new() { Style = (Style)Application.Current.Resources["Card"] };
    private static TextBlock Label(string text, double size = 14, string color = "#F5F4FA") => new() { Text = text, FontSize = size, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Body(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(165, 163, 181)), FontSize = 13, LineHeight = 25, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private static FrameworkElement Heading(string title, string subtitle) { var stack = Stack(); stack.Margin = new(0, 0, 0, 18); stack.Children.Add(Label(title, 28)); stack.Children.Add(Body(subtitle)); return stack; }
    private static Border Badge(string text, string background, string foreground) => new() { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)), CornerRadius = new(12), Padding = new(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0), Child = Label(text, 11, foreground) };
    private static Border Metric(string name, string value, string hint, string accent) { var card = Card(); var stack = Stack(); stack.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)), HorizontalAlignment = HorizontalAlignment.Left }); stack.Children.Add(Label(name, 13, "#A5A3B5")); var metric = Label(value, 24); metric.Margin = new(0, 8, 0, 3); stack.Children.Add(metric); stack.Children.Add(Label(hint, 11, "#777589")); card.Child = stack; return card; }
    private Button ActionButton(string text) { var button = MakeButton(text, false); button.Tag = text; button.Click += QuickAction_Click; return button; }
    private static Border Empty(string body, string title) { var border = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 24, 33)), CornerRadius = new(10), Padding = new(20), Margin = new(0, 14, 0, 0) }; var stack = Stack(); stack.Children.Add(Label(title, 14)); stack.Children.Add(Body(body)); border.Child = stack; return border; }
    private static FrameworkElement Row(string name, string value) { var grid = new Grid { Margin = new(0, 0, 0, 12) }; grid.ColumnDefinitions.Add(new() { Width = new(180) }); grid.ColumnDefinitions.Add(new()); grid.Children.Add(Label(name, 13, "#A5A3B5")); var text = Label(value, 13); Grid.SetColumn(text, 1); grid.Children.Add(text); return grid; }
    private static FrameworkElement Section(string name, string content, string status) { var card = Card(); var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(180) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(240) }); grid.Children.Add(Label(name, 15)); var detail = Body(content); detail.Margin = new(0); Grid.SetColumn(detail, 1); grid.Children.Add(detail); var state = Label(status, 12, "#A5A3B5"); state.TextAlignment = TextAlignment.Right; Grid.SetColumn(state, 2); grid.Children.Add(state); card.Child = grid; return card; }
    private static Button MakeButton(string label, bool primary) => new() { Content = label, Style = (Style)Application.Current.Resources[primary ? "PrimaryButton" : "SecondaryButton"], Margin = new(0, 0, 9, 0), Padding = new(13, 8, 13, 8) };
    private static FrameworkElement ActionBar(params (string Label, RoutedEventHandler Handler, bool Primary)[] actions) { var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) }; foreach (var action in actions) { var button = MakeButton(action.Label, action.Primary); button.Click += action.Handler; stack.Children.Add(button); } return stack; }
    private static FrameworkElement Page(string title, string subtitle, params FrameworkElement[] children) { var stack = Stack(); stack.Children.Add(Heading(title, subtitle)); foreach (var child in children) { if (child is Border { Style: not null }) stack.Children.Add(child); else { var card = Card(); var inner = Stack(); inner.Children.Add(child); card.Child = inner; stack.Children.Add(card); } } return stack; }
}
