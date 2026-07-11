using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _statusTimer;
    private CheckBox? _privacyCheck;
    private CheckBox? _startupCheck;
    private TextBox? _fastIntervalBox;
    private TextBox? _staticIntervalBox;
    private UpdateInfo? _updateInfo;
    private TextBlock? _updateStatusLabel;
    private Button? _installUpdateButton;
    private readonly string _dataDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent");

    public MainWindow(AgentSettings settings, SettingsStore settingsStore, ICredentialStore credentials, bool demo = false)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _demo = demo;
        VersionFooter.Text = $"Agent {UpdateChecker.CurrentVersion}  •  Protocol 1";
        PageContent.Content = BuildOverview();
        RefreshServiceStatus();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshServiceStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
        Loaded += async (_, _) =>
        {
            await EnsureServiceInstalledAsync(promptIfMissing: true);
            await CheckForUpdatesQuietAsync();
        };
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
        var snapshot = ReadConnectionSnapshot();
        var installed = _demo || IsServiceInstalled();
        var root = Stack();
        root.Children.Add(Heading(_settings.DeviceName, "Your Windows PC, connected without opening an inbound port."));
        var hero = Card();
        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = Stack();
        identity.Children.Add(Label("HOME ASSISTANT", 11, "#A5A3B5"));
        identity.Children.Add(Label(string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) ? "Not configured" : _settings.HomeAssistantUrl, 16));
        identity.Children.Add(Badge(
            installed ? StatusBadgeText(snapshot) : "●  Background service not installed",
            installed ? StatusBadgeBackground(snapshot) : "#4A1E1E",
            installed ? StatusBadgeForeground(snapshot) : "#F0A0A0"));
        if (!installed)
            identity.Children.Add(Body("The desktop app only manages settings. Install the background Windows service from the Home Assistant page to connect."));
        else if (!string.IsNullOrWhiteSpace(snapshot?.FriendlyError) && snapshot.Status != ConnectionStatus.Connected)
            identity.Children.Add(Body(snapshot.FriendlyError));
        heroGrid.Children.Add(identity);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in new[] { "Lock", "Sleep", "Hibernate", "Log off", "Restart", "Shut down" }) actions.Children.Add(ActionButton(action));
        Grid.SetColumn(actions, 1);
        heroGrid.Children.Add(actions);
        hero.Child = heroGrid;
        root.Children.Add(hero);
        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(Metric("CPU", _demo ? "18%" : "Live in HA", $"Fast · {_settings.FastUpdateSeconds}s", "#7568FF"));
        metrics.Children.Add(Metric("Memory", _demo ? "42%" : "Live in HA", "Change-filtered", "#41C7A5"));
        metrics.Children.Add(Metric("Volume", _demo ? "35%" : "Live in HA", "Default output", "#49A8FF"));
        metrics.Children.Add(Metric("Online", "Live in HA", "binary_sensor.online", "#F0A53A"));
        root.Children.Add(metrics);
        var lower = new Grid();
        lower.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var activity = Card();
        var activityContent = Stack();
        activityContent.Children.Add(Label("Get started", 16));
        activityContent.Children.Add(Body("Edit the Home Assistant connection and choose Sensors and Controls. The agent reloads settings automatically; use Restart agent only if the service is stuck."));
        if (_updateInfo?.IsUpdateAvailable == true)
            activityContent.Children.Add(Body($"Update available: v{_updateInfo.LatestVersion}. Open Settings → Check for updates to install."));
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
        var snapshot = ReadConnectionSnapshot();
        var installed = _demo || IsServiceInstalled();
        var actions = ActionBar(
            ("Edit connection", EditConnection_Click, true),
            ("Test saved connection", TestConnection_Click, false),
            installed ? ("Restart agent", RestartAgent_Click, false) : ("Install background service", InstallService_Click, false),
            ("Remove credential", RemoveCredential_Click, false));
        return Page("Home Assistant connection", "Edit and validate the URL, credential, and PC name here. The background Windows service is what actually connects to Home Assistant.",
            Row("Server", string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) ? "Not configured" : _settings.HomeAssistantUrl),
            Row("PC name", _settings.DeviceName),
            Row("Background service", installed ? (IsServiceRunning() ? "Installed and running" : "Installed but stopped") : "Not installed"),
            Row("Live status", StatusLabel(snapshot)),
            Row("Last connected", snapshot?.LastConnected is null ? "Never" : snapshot.LastConnected.Value.ToLocalTime().ToString("g")),
            Row("Installation ID", _settings.InstallationId),
            Row("Protocol", "Version 1"),
            Row("Authentication", "Credential protected by Windows DPAPI"),
            string.IsNullOrWhiteSpace(snapshot?.FriendlyError) && installed ? actions : StackWith(
                Body(installed
                    ? snapshot?.FriendlyError ?? string.Empty
                    : "The desktop app only manages settings. Install the background Windows service to connect this PC to Home Assistant."),
                actions));
    }

    private FrameworkElement BuildSensorPage()
    {
        _sensorChecks.Clear();
        var system = SensorToggle("system", "System", "CPU, memory, uptime, idle time, lock and power state");
        var audio = SensorToggle("audio", "Audio", "Volume, mute and default output device");
        var network = SensorToggle("network", "Network", "Local IP plus upload and download rate");
        var storage = SensorToggle("storage", "Storage", "System drive free space — sampled on the slow interval");
        return Page("Sensor settings", "Fast sensors update often. Slow sensors (storage) update less often and only push when values change.",
            system, audio, network, storage,
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
            ControlToggle("system.hibernate", "Hibernate", "Hibernate the PC — off by default"),
            ControlToggle("system.logoff", "Log off", "Sign out the current user — off by default"),
            ControlToggle("system.restart", "Restart", "Restart the PC — off by default"),
            ControlToggle("system.shutdown", "Shut down", "Shut down the PC — off by default"),
            ControlToggle("system.display_off", "Turn display off", "Blank the monitor"),
            ControlToggle("system.abort_shutdown", "Cancel shutdown", "Abort a pending shutdown/restart"),
            ControlToggle("system.open_explorer", "Open File Explorer", "Open Explorer on the PC"),
            ControlToggle("system.open_settings", "Open Settings", "Open Windows Settings"),
            ControlToggle("audio.set_volume", "Volume control", "Allow Home Assistant to set volume"),
            ControlToggle("audio.set_mute", "Mute control", "Allow Home Assistant to mute or unmute"),
            ControlToggle("app.launch", "Launch applications", "Allow Home Assistant to launch allowlisted apps"),
            ControlToggle("custom.run", "Custom commands", "Allow Home Assistant to run allowlisted custom commands")
        };
        return Page("Approved controls", "Turn a control on, save, and the agent reconnects. Enabled controls show as active buttons in Home Assistant (integration 0.1.6+).",
            controls.Append(ActionBar(("Save control settings", SaveControls_Click, true))).ToArray());
    }

    private FrameworkElement BuildApplications()
    {
        var list = Stack();
        if (_settings.AllowedApplications.Count == 0)
            list.Children.Add(Empty("Add apps you want Home Assistant to launch. Only these exact executables can be started — HA cannot pick a different path.", "No applications yet"));
        foreach (var app in _settings.AllowedApplications.ToList())
        {
            var row = Card();
            var content = Stack();
            content.Children.Add(Label(string.IsNullOrWhiteSpace(app.Name) ? "Unnamed app" : app.Name, 15));
            content.Children.Add(Body($"{app.ExecutablePath}\n{(string.IsNullOrWhiteSpace(app.Arguments) ? "No arguments" : "Args: " + app.Arguments)}\n{(app.Enabled ? "Enabled" : "Disabled")}"));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
            var toggle = MakeButton(app.Enabled ? "Disable" : "Enable", false);
            toggle.Click += async (_, _) => { app.Enabled = !app.Enabled; await _settingsStore.SaveAsync(_settings); PageContent.Content = BuildApplications(); };
            var remove = MakeButton("Remove", false);
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show($"Remove {app.Name} from the allowlist?", "Remove application", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _settings.AllowedApplications.Remove(app);
                await _settingsStore.SaveAsync(_settings);
                PageContent.Content = BuildApplications();
            };
            buttons.Children.Add(toggle);
            buttons.Children.Add(remove);
            content.Children.Add(buttons);
            row.Child = content;
            list.Children.Add(row);
        }
        return Page("Application allowlist", "Each enabled app becomes a Home Assistant button. Paths are chosen on this PC only.",
            list,
            ActionBar(("Add application", AddApplication_Click, true), ("Save & reconnect", SaveAppsCommands_Click, false)));
    }

    private FrameworkElement BuildCommands()
    {
        var list = Stack();
        list.Children.Add(Badge("ALLOWLIST ONLY  No remote shell — HA can only run commands you add here", "#1E4A3E", "#73E2B9"));
        if (_settings.CustomCommands.Count == 0)
            list.Children.Add(Empty("Add fixed executables (optionally with admin elevation). Home Assistant only sends the command id — never a free-form path.", "No custom commands yet"));
        foreach (var command in _settings.CustomCommands.ToList())
        {
            var row = Card();
            var content = Stack();
            content.Children.Add(Label(string.IsNullOrWhiteSpace(command.Name) ? "Unnamed command" : command.Name, 15));
            content.Children.Add(Body($"{command.ExecutablePath}\n{(string.IsNullOrWhiteSpace(command.Arguments) ? "No arguments" : "Args: " + command.Arguments)}\n{(command.RequiresElevation ? "Runs elevated (UAC)" : "Normal privileges")} • {(command.Enabled ? "Enabled" : "Disabled")}"));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
            var toggle = MakeButton(command.Enabled ? "Disable" : "Enable", false);
            toggle.Click += async (_, _) => { command.Enabled = !command.Enabled; await _settingsStore.SaveAsync(_settings); PageContent.Content = BuildCommands(); };
            var remove = MakeButton("Remove", false);
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show($"Remove {command.Name}?", "Remove command", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _settings.CustomCommands.Remove(command);
                await _settingsStore.SaveAsync(_settings);
                PageContent.Content = BuildCommands();
            };
            buttons.Children.Add(toggle);
            buttons.Children.Add(remove);
            content.Children.Add(buttons);
            row.Child = content;
            list.Children.Add(row);
        }
        return Page("Custom commands", "Enable Custom commands on the Controls page before Home Assistant can run these. Elevation prompts UAC on this PC.",
            list,
            ActionBar(("Add custom command", AddCustomCommand_Click, true), ("Save & reconnect", SaveAppsCommands_Click, false)));
    }

    private FrameworkElement BuildLogs() => Page("Logs & diagnostics", "Open local rotating logs or export a redacted configuration snapshot.",
        Row("Log level", "Information"),
        Row("Retention", "14 rolling files"),
        ActionBar(("Open log folder", OpenLogs_Click, true), ("Export diagnostics", ExportDiagnostics_Click, false)));

    private FrameworkElement BuildSettings()
    {
        _startupCheck = Toggle("Start the Windows service automatically", "Configure the installed PC Bridge Agent service to start at boot", _settings.StartAutomatically);
        _privacyCheck = Toggle("Privacy-sensitive sensors", "Master permission for future activity, user, microphone and camera providers", _settings.PrivacySensorsEnabled);
        _fastIntervalBox = new TextBox { Text = _settings.FastUpdateSeconds.ToString(), Margin = new(0, 0, 0, 8), Padding = new(8, 6, 8, 6) };
        _staticIntervalBox = new TextBox { Text = _settings.StaticUpdateSeconds.ToString(), Margin = new(0, 0, 0, 8), Padding = new(8, 6, 8, 6) };
        var intervals = Card();
        var intervalStack = Stack();
        intervalStack.Children.Add(Label("Update intervals", 16));
        intervalStack.Children.Add(Body("Fast sensors (CPU, memory, audio, network): seconds between samples. Only changed values are pushed."));
        intervalStack.Children.Add(_fastIntervalBox);
        intervalStack.Children.Add(Body("Slow sensors (storage): seconds between samples."));
        intervalStack.Children.Add(_staticIntervalBox);
        intervals.Child = intervalStack;

        var updates = Card();
        var updateStack = Stack();
        updateStack.Children.Add(Label("Software updates", 16));
        updateStack.Children.Add(Body("Check GitHub for a newer PC Bridge Agent installer. Settings and your encrypted token are kept when you upgrade."));
        _updateStatusLabel = Body(FormatUpdateStatus());
        updateStack.Children.Add(_updateStatusLabel);
        _installUpdateButton = MakeButton("Download & install update", true);
        _installUpdateButton.IsEnabled = _updateInfo?.IsUpdateAvailable == true && !string.IsNullOrWhiteSpace(_updateInfo.InstallerUrl);
        _installUpdateButton.Click += InstallUpdate_Click;
        var updateButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
        var checkBtn = MakeButton("Check for updates", true);
        checkBtn.Click += CheckForUpdates_Click;
        var releaseBtn = MakeButton("Open releases page", false);
        releaseBtn.Click += (_, _) => UpdateChecker.OpenUrl(_updateInfo?.ReleaseUrl ?? UpdateChecker.AppReleasesUrl);
        var haBtn = MakeButton("How to update HA", false);
        haBtn.Click += (_, _) => MessageBox.Show(
            $"{UpdateChecker.HaHacsHint}\n\nOr open: {UpdateChecker.HaReleasesUrl}",
            "Update Home Assistant integration",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        updateButtons.Children.Add(checkBtn);
        updateButtons.Children.Add(_installUpdateButton);
        updateButtons.Children.Add(releaseBtn);
        updateButtons.Children.Add(haBtn);
        updateStack.Children.Add(updateButtons);
        updates.Child = updateStack;

        return Page("Settings", "These settings are saved locally on this PC.",
            _startupCheck,
            _privacyCheck,
            intervals,
            updates,
            ActionBar(("Save settings", SaveGeneralSettings_Click, true), ("Open Windows Services", OpenServices_Click, false)));
    }

    private string FormatUpdateStatus()
    {
        if (_updateInfo is null) return $"Installed version: {UpdateChecker.CurrentVersion}. Not checked yet.";
        if (_updateInfo.IsUpdateAvailable)
            return $"Installed: {_updateInfo.CurrentVersion}  →  Latest: {_updateInfo.LatestVersion}. An update is available.";
        return $"Installed: {_updateInfo.CurrentVersion}. You are up to date.";
    }

    private async Task CheckForUpdatesQuietAsync()
    {
        if (_demo) return;
        try
        {
            _updateInfo = await UpdateChecker.CheckAsync();
            if (PageTitle.Text is "Overview" or "Settings")
                PageContent.Content = PageTitle.Text == "Overview" ? BuildOverview() : BuildSettings();
        }
        catch
        {
            // Quiet background check — user can retry from Settings.
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_demo)
        {
            MessageBox.Show("Demo mode cannot check GitHub for updates.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (_updateStatusLabel is not null) _updateStatusLabel.Text = "Checking GitHub for the latest release…";
            _updateInfo = await UpdateChecker.CheckAsync();
            if (_updateStatusLabel is not null) _updateStatusLabel.Text = FormatUpdateStatus();
            if (_installUpdateButton is not null)
                _installUpdateButton.IsEnabled = _updateInfo.IsUpdateAvailable && !string.IsNullOrWhiteSpace(_updateInfo.InstallerUrl);
            if (_updateInfo.IsUpdateAvailable)
                MessageBox.Show($"Version {_updateInfo.LatestVersion} is available.\n\nSelect Download & install update to upgrade. Your settings and token stay on this PC.", "Update available", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"You already have the latest version ({_updateInfo.CurrentVersion}).", "Up to date", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not check for updates.\n\n{ex.Message}\n\nYou can still download from:\n{UpdateChecker.AppReleasesUrl}", "Update check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateChecker.OpenUrl(UpdateChecker.AppReleasesUrl);
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo is null || !_updateInfo.IsUpdateAvailable || string.IsNullOrWhiteSpace(_updateInfo.InstallerUrl))
        {
            MessageBox.Show("Check for updates first.", "No update ready", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                $"Download and install PC Bridge Agent {_updateInfo.LatestVersion}?\n\nThe installer will ask for administrator approval. Close this window after the installer starts if it asks you to.",
                "Install update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            if (_updateStatusLabel is not null) _updateStatusLabel.Text = "Downloading installer…";
            if (_installUpdateButton is not null) _installUpdateButton.IsEnabled = false;
            var progress = new Progress<double>(p =>
            {
                if (_updateStatusLabel is not null) _updateStatusLabel.Text = $"Downloading installer… {p:0}%";
            });
            var path = await UpdateChecker.DownloadInstallerAsync(_updateInfo.InstallerUrl, progress);
            if (_updateStatusLabel is not null) _updateStatusLabel.Text = "Starting installer…";
            UpdateChecker.LaunchInstaller(path);
            MessageBox.Show("The installer started. Approve the Windows prompt, finish setup, then reopen PC Bridge Agent.", "Installer started", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception)
        {
            MessageBox.Show("Administrator approval was cancelled. The update was not installed.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            if (_installUpdateButton is not null) _installUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Download or install failed.\n\n{ex.Message}\n\nOpening the releases page instead.", "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateChecker.OpenUrl(_updateInfo.ReleaseUrl);
            if (_installUpdateButton is not null) _installUpdateButton.IsEnabled = true;
        }
    }

    private FrameworkElement BuildComingSoon(string title) => Page(title, "This feature is not implemented in the current release.",
        Empty("The page is intentionally read-only so it does not imply an action succeeded.", "Coming later"));

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*", Title = "Choose application" };
        if (dialog.ShowDialog(this) != true) return;
        var name = PromptText("Display name for Home Assistant:", System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        var args = PromptText("Optional arguments:", "") ?? string.Empty;
        _settings.AllowedApplications.Add(new AllowedApplication
        {
            Name = name.Trim(),
            ExecutablePath = dialog.FileName,
            Arguments = args.Trim(),
            Enabled = true
        });
        await _settingsStore.SaveAsync(_settings);
        PageContent.Content = BuildApplications();
    }

    private async void AddCustomCommand_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.ps1|All files|*.*", Title = "Choose command executable" };
        if (dialog.ShowDialog(this) != true) return;
        var name = PromptText("Display name for Home Assistant:", System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        var args = PromptText("Fixed arguments (Home Assistant cannot change these):", "") ?? string.Empty;
        var elevate = MessageBox.Show("Should this command request administrator privileges (UAC) when run?", "Elevation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _settings.CustomCommands.Add(new CustomCommand
        {
            Name = name.Trim(),
            ExecutablePath = dialog.FileName,
            Arguments = args.Trim(),
            RequiresElevation = elevate,
            Enabled = true
        });
        _settings.EnabledControls["custom.run"] = true;
        MessageBox.Show("Custom command saved and “Custom commands” was enabled on the Controls page. Home Assistant will show a new button after the agent reconnects (HA integration 0.1.6+).", "Command added", MessageBoxButton.OK, MessageBoxImage.Information);
        await _settingsStore.SaveAsync(_settings);
        PageContent.Content = BuildCommands();
    }

    private string? PromptText(string label, string initial)
    {
        var window = new Window
        {
            Title = "PC Bridge Agent",
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 23))
        };
        var box = new TextBox { Text = initial, Margin = new(0, 8, 0, 12), Padding = new(8, 6, 8, 6) };
        string? result = null;
        var ok = MakeButton("Save", true);
        ok.Click += (_, _) => { result = box.Text; window.DialogResult = true; };
        var cancel = MakeButton("Cancel", false);
        cancel.Click += (_, _) => window.DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var stack = Stack();
        stack.Margin = new(16);
        stack.Children.Add(Label(label, 13, "#A5A3B5"));
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        window.Content = stack;
        return window.ShowDialog() == true ? result : null;
    }

    private async void SaveAppsCommands_Click(object sender, RoutedEventArgs e)
    {
        await _settingsStore.SaveAsync(_settings);
        MessageBox.Show("Saved. The agent will reconnect and register the updated buttons in Home Assistant.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }
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

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SetupWindow(_settingsStore, _credentials, _settings) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            PageContent.Content = BuildConnection();
            RefreshServiceStatus();
            MessageBox.Show("Connection saved. The background agent will reconnect automatically within a few seconds.", "Connection saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = await _credentials.GetTokenAsync() ?? string.Empty;
            await HomeAssistantConnectionValidator.ValidateAsync(_settings.HomeAssistantUrl, token);
            MessageBox.Show("Home Assistant accepted the saved URL and credential, and the PC Bridge integration is available.", "Connection successful", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async void RestartAgent_Click(object sender, RoutedEventArgs e)
    {
        if (!IsServiceInstalled()) await EnsureServiceInstalledAsync(promptIfMissing: false);
        else await RestartServiceElevatedAsync();
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e) => await EnsureServiceInstalledAsync(promptIfMissing: false);

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
        if (int.TryParse(_fastIntervalBox?.Text, out var fast)) _settings.FastUpdateSeconds = fast;
        if (int.TryParse(_staticIntervalBox?.Text, out var slow)) _settings.StaticUpdateSeconds = slow;
        await _settingsStore.SaveAsync(_settings);
        if (autoStartChanged && !_demo)
        {
            try
            {
                if (!IsServiceInstalled()) await InstallServiceElevatedAsync();
                else await RunElevatedAsync("sc.exe", $"config \"{ServiceName}\" start= {(_settings.StartAutomatically ? "auto" : "demand")}");
            }
            catch (Exception ex) { MessageBox.Show($"Settings were saved, but Windows service startup could not be changed: {ex.Message}", "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        MessageBox.Show("Settings saved. Interval changes apply when the agent reconnects.", "PC Bridge Agent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveAndOfferRestartAsync(string message)
    {
        await _settingsStore.SaveAsync(_settings);
        MessageBox.Show($"{message}\n\nThe background agent will reload these settings automatically.", "Settings saved", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshServiceStatus();
    }

    private async Task EnsureServiceInstalledAsync(bool promptIfMissing)
    {
        if (_demo || IsServiceInstalled()) return;
        if (promptIfMissing)
        {
            var answer = MessageBox.Show(
                "PC Bridge's background Windows service is not installed, so this PC cannot connect to Home Assistant.\n\nInstall and start it now? Administrator approval is required.",
                "Background service missing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        await InstallServiceElevatedAsync();
    }

    private async Task InstallServiceElevatedAsync()
    {
        try
        {
            var serviceExe = ResolveServiceExecutable();
            if (!System.IO.File.Exists(serviceExe))
            {
                MessageBox.Show("Could not find PcBridge.Agent.Service.exe next to the app. Reinstall PC Bridge Agent.", "Service missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var escaped = serviceExe.Replace("'", "''");
            var script =
                "$ErrorActionPreference = 'Stop'\n" +
                "$name = 'PC Bridge Agent'\n" +
                "$bin = '" + escaped + "'\n" +
                "$existing = Get-Service -Name $name -ErrorAction SilentlyContinue\n" +
                "if (-not $existing) {\n" +
                "  New-Service -Name $name -BinaryPathName \"`\"$bin`\"\" -DisplayName $name -StartupType Automatic -Description 'Secure outbound Windows-to-Home-Assistant bridge' | Out-Null\n" +
                "} else {\n" +
                "  Stop-Service -Name $name -Force -ErrorAction SilentlyContinue\n" +
                "  sc.exe config $name binPath= \"`\"$bin`\"\" start= auto | Out-Null\n" +
                "}\n" +
                "sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null\n" +
                "Start-Service -Name $name -ErrorAction Stop\n" +
                "(Get-Service $name).WaitForStatus('Running', '00:00:45')\n";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            await RunElevatedAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}");
            await Task.Delay(1500);
            RefreshServiceStatus();
            if (PageTitle.Text == "Home Assistant") PageContent.Content = BuildConnection();
            MessageBox.Show("The background service is installed and running. Home Assistant should show this PC within a few seconds.", "Service installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("Administrator approval was cancelled, so the background service was not installed.", "Install cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The background service could not be installed.\n\n{ex.Message}", "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ResolveServiceExecutable()
    {
        var appDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return System.IO.Path.Combine(appDir, "service", "PcBridge.Agent.Service.exe");
    }

    private async Task RestartServiceElevatedAsync()
    {
        if (_demo) { MessageBox.Show("Demo: agent service restarted.", "PC Bridge Agent"); return; }
        if (!IsServiceInstalled())
        {
            await InstallServiceElevatedAsync();
            return;
        }
        try
        {
            // PowerShell waits for stop/start correctly; the old sc+timeout chain often failed mid-restart.
            const string script = """
$ErrorActionPreference = 'Stop'
$name = 'PC Bridge Agent'
$svc = Get-Service -Name $name -ErrorAction Stop
if ($svc.Status -ne 'Stopped') {
  Stop-Service -Name $name -Force -ErrorAction Stop
  $svc.WaitForStatus('Stopped', '00:00:45')
}
Start-Service -Name $name -ErrorAction Stop
$svc.Refresh()
$svc.WaitForStatus('Running', '00:00:45')
if ($svc.Status -ne 'Running') { throw "Service status is $($svc.Status)" }
""";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            await RunElevatedAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}");
            await Task.Delay(1500);
            RefreshServiceStatus();
            var snapshot = ReadConnectionSnapshot();
            MessageBox.Show(
                snapshot?.Status == ConnectionStatus.Connected
                    ? "The PC Bridge Agent service was restarted and is connected to Home Assistant."
                    : "The PC Bridge Agent service was restarted. Live connection status will update in a few seconds.",
                "Agent restarted", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (_demo)
        {
            AgentStatusText.Text = "Demo mode";
            AgentStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#44D19D"));
            return;
        }
        if (!IsServiceInstalled())
        {
            AgentStatusText.Text = "Background service not installed";
            AgentStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F06D6D"));
            return;
        }
        if (!IsServiceRunning())
        {
            AgentStatusText.Text = "Agent service stopped";
            AgentStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F06D6D"));
            return;
        }
        var snapshot = ReadConnectionSnapshot();
        AgentStatusText.Text = snapshot?.Status switch
        {
            ConnectionStatus.Connected => "Connected to Home Assistant",
            ConnectionStatus.Connecting => "Connecting to Home Assistant…",
            ConnectionStatus.AuthenticationFailed => "Authentication failed",
            ConnectionStatus.Incompatible => "HA integration missing/incompatible",
            _ => "Agent running — waiting for HA"
        };
        AgentStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(snapshot?.Status switch
        {
            ConnectionStatus.Connected => "#44D19D",
            ConnectionStatus.Connecting => "#F0A53A",
            ConnectionStatus.AuthenticationFailed or ConnectionStatus.Incompatible => "#F06D6D",
            _ => "#F0A53A"
        }));
    }

    private ConnectionSnapshot? ReadConnectionSnapshot() =>
        _demo ? new ConnectionSnapshot(ConnectionStatus.Connected, DateTimeOffset.UtcNow) : FileConnectionStatusStore.TryRead(_dataDirectory);

    private static string StatusLabel(ConnectionSnapshot? snapshot) => snapshot?.Status switch
    {
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.Connecting => "Connecting",
        ConnectionStatus.AuthenticationFailed => "Authentication failed",
        ConnectionStatus.Incompatible => "Integration missing or incompatible",
        ConnectionStatus.Disconnected => "Disconnected",
        _ => "Unknown"
    };

    private static string StatusBadgeText(ConnectionSnapshot? snapshot) => snapshot?.Status switch
    {
        ConnectionStatus.Connected => "●  Connected to Home Assistant",
        ConnectionStatus.Connecting => "●  Connecting…",
        ConnectionStatus.AuthenticationFailed => "●  Authentication failed",
        ConnectionStatus.Incompatible => "●  Install PC Bridge in Home Assistant",
        _ => "●  Waiting for Home Assistant"
    };

    private static string StatusBadgeBackground(ConnectionSnapshot? snapshot) => snapshot?.Status == ConnectionStatus.Connected ? "#1E4A3E" : "#4A1E1E";
    private static string StatusBadgeForeground(ConnectionSnapshot? snapshot) => snapshot?.Status == ConnectionStatus.Connected ? "#73E2B9" : "#F0A0A0";

    private static bool IsServiceInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"query \"{ServiceName}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch { return false; }
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
        var snapshot = ReadConnectionSnapshot();
        var diagnostics = new
        {
            generated_at = DateTimeOffset.UtcNow,
            agent_version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3),
            installation_id = _settings.InstallationId,
            home_assistant_configured = !string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl),
            service_running = IsServiceRunning(),
            service_installed = IsServiceInstalled(),
            connection_status = snapshot?.Status.ToString(),
            last_connected = snapshot?.LastConnected,
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
        var path = System.IO.Path.Combine(_dataDirectory, "logs");
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        var action = (string)((Button)sender).Tag;
        var command = action switch
        {
            "Lock" => "system.lock",
            "Sleep" => "system.sleep",
            "Hibernate" => "system.hibernate",
            "Log off" => "system.logoff",
            "Restart" => "system.restart",
            _ => "system.shutdown"
        };
        if (!_settings.EnabledControls.GetValueOrDefault(command)) { MessageBox.Show($"{action} is disabled on the Controls page.", "Control disabled", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (action is "Restart" or "Shut down" or "Hibernate" or "Log off" && MessageBox.Show($"Are you sure you want to {action.ToLowerInvariant()} this PC?", "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_demo) { MessageBox.Show($"Demo: {action} command accepted.", "PC Bridge Agent"); return; }
        var result = await new PowerCommandHandler().ExecuteAsync(command, null, CancellationToken.None);
        if (!result.Success) MessageBox.Show(result.Message, "Command failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static StackPanel Stack() => new() { Margin = new Thickness(0) };
    private static FrameworkElement StackWith(params FrameworkElement[] children) { var stack = Stack(); foreach (var child in children) stack.Children.Add(child); return stack; }
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
