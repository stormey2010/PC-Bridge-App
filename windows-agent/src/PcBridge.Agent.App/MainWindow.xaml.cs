using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PcBridge.Agent.Core;
using PcBridge.Agent.Windows;

namespace PcBridge.Agent.App;

public partial class MainWindow : Window
{
    private readonly AgentSettings _settings;
    private readonly bool _demo;
    public MainWindow(AgentSettings settings, bool demo = false) { InitializeComponent(); _settings = settings; _demo = demo; PageContent.Content = BuildOverview(); }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in Navigation.Children.OfType<Button>()) child.Tag = null;
        var button = (Button)sender; button.Tag = "selected";
        var title = button.Content.ToString()!.Split("   ").Last(); PageTitle.Text = title;
        PageContent.Content = title switch
        {
            "Overview" => BuildOverview(), "Home Assistant" => BuildConnection(), "Sensors" => BuildSensorPage(),
            "Controls" => BuildControls(), "Applications" => BuildApplications(), "Commands" => BuildCommands(),
            "Logs" => BuildLogs(), "Settings" => BuildSettings(), _ => BuildComingSoon(title)
        };
    }

    private FrameworkElement BuildOverview()
    {
        var root = Stack(); root.Children.Add(Heading(_settings.DeviceName, "Your Windows PC, connected without opening an inbound port."));
        var hero = Card(); var heroGrid = new Grid(); heroGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heroGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = Stack(); identity.Children.Add(Label("HOME ASSISTANT", 11, "#A5A3B5")); identity.Children.Add(Label(string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) ? "Not configured" : _settings.HomeAssistantUrl, 16)); identity.Children.Add(Badge("●  Connection managed by background agent", "#1E4A3E", "#73E2B9")); heroGrid.Children.Add(identity);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; foreach (var action in new[] { "Lock", "Sleep", "Restart", "Shut down" }) actions.Children.Add(ActionButton(action)); Grid.SetColumn(actions, 1); heroGrid.Children.Add(actions); hero.Child = heroGrid; root.Children.Add(hero);
        var metrics = new UniformGrid { Columns = 4 }; metrics.Children.Add(Metric("CPU", _demo ? "18%" : "—", "Updates every 5 sec", "#7568FF")); metrics.Children.Add(Metric("Memory", _demo ? "42%" : "—", "Event stream ready", "#41C7A5")); metrics.Children.Add(Metric("Volume", _demo ? "35%" : "—", "Speakers (Realtek)", "#49A8FF")); metrics.Children.Add(Metric("Keep awake", "Off", "System may sleep", "#F0A53A")); root.Children.Add(metrics);
        var lower = new Grid(); lower.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) }); lower.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var activity = Card(); var a = Stack(); a.Children.Add(Label("Recent activity", 16)); a.Children.Add(Empty("Live commands and connection events will appear here.", "No recent activity")); activity.Child = a; lower.Children.Add(activity);
        var security = Card(); var s = Stack(); s.Children.Add(Label("Secure by default", 16)); s.Children.Add(Body("Outbound TLS connection\nCredential protected by Windows DPAPI\nNo generic remote shell\nDestructive controls disabled locally")); security.Child = s; Grid.SetColumn(security, 1); lower.Children.Add(security); root.Children.Add(lower); return root;
    }

    private FrameworkElement BuildConnection() => Page("Home Assistant connection", "The agent authenticates over an outbound WebSocket and reconnects automatically.",
        Row("Server", _settings.HomeAssistantUrl), Row("Installation ID", _settings.InstallationId), Row("Protocol", "Version 1"), Row("Authentication", "Credential stored with Windows DPAPI"), ButtonBar("Test connection", "Reconnect", "Replace credential", "Remove pairing"));
    private FrameworkElement BuildSensorPage() => Page("Sensor catalog", "Unsupported hardware is labeled instead of reported with fake values.",
        Section("System", "CPU usage  •  Memory  •  Uptime  •  Idle time", "Event + 5 second sampling"), Section("Audio", "Volume  •  Mute  •  Default output", "5 second sampling"), Section("Network", "Local IP  •  Download  •  Upload", "5 second sampling"), Section("Hardware monitoring", "CPU/GPU temperature", "Unsupported — no provider detected"));
    private FrameworkElement BuildControls() => Page("Approved controls", "Every remote command is validated again by the Windows agent.",
        Section("Session", "Lock", "Enabled"), Section("Power", "Sleep", "Enabled"), Section("Destructive actions", "Restart  •  Shut down", "Disabled — enable locally after reviewing confirmations"), Section("Keep awake", "System awake  •  System + display  •  Timed modes", "Enabled"));
    private FrameworkElement BuildApplications() => Page("Application allowlist", "Only applications added locally can be controlled from Home Assistant.", Empty("Add application controls in Phase 2. Arbitrary executable launch is never enabled by default.", "No approved applications"), ButtonBar("Add application"));
    private FrameworkElement BuildCommands() => Page("Commands", "Safe commands are allowlisted. Advanced Command Mode is planned for Phase 3 and is unavailable in this secure foundation.", Badge("SHIELDED  Safe Commands active", "#1E4A3E", "#73E2B9"), Empty("This Phase 1 build intentionally exposes no generic shell.", "Advanced Mode disabled"));
    private FrameworkElement BuildLogs() => Page("Logs & diagnostics", "Rotating structured logs are retained for 14 days and redact credentials.", Row("Log level", "Information"), Row("Retention", "14 rolling files"), ButtonBar("Open log folder", "Export diagnostics"));
    private FrameworkElement BuildSettings() => Page("Settings", "Appearance and startup preferences apply to the desktop companion.", Section("Appearance", "Dark theme  •  Violet accent  •  Comfortable density", "System theme support in next UI update"), Section("Startup", "Background Windows service", _settings.StartAutomatically ? "Starts automatically" : "Manual start"), Section("Privacy", "Active app  •  Window titles  •  Current user", _settings.PrivacySensorsEnabled ? "Enabled" : "Disabled by default"));
    private FrameworkElement BuildComingSoon(string title) => Page(title, "This surface is reserved for the next implementation phase.", Empty("Core automation events are already available through Home Assistant entities.", "Nothing configured yet"));

    private static StackPanel Stack() => new() { Margin = new Thickness(0) };
    private static Border Card() => new() { Style = (Style)Application.Current.Resources["Card"] };
    private static TextBlock Label(string text, double size = 14, string color = "#F5F4FA") => new() { Text = text, FontSize = size, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Body(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(165, 163, 181)), FontSize = 13, LineHeight = 25, Margin = new Thickness(0, 10, 0, 0) };
    private static FrameworkElement Heading(string title, string subtitle) { var s = Stack(); s.Margin = new(0, 0, 0, 18); s.Children.Add(Label(title, 28)); s.Children.Add(Body(subtitle)); return s; }
    private static Border Badge(string text, string background, string foreground) => new() { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)), CornerRadius = new(12), Padding = new(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0), Child = Label(text, 11, foreground) };
    private static Border Metric(string name, string value, string hint, string accent) { var b = Card(); var s = Stack(); var dot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)), HorizontalAlignment = HorizontalAlignment.Left }; s.Children.Add(dot); s.Children.Add(Label(name, 13, "#A5A3B5")); var v = Label(value, 28); v.Margin = new(0, 8, 0, 3); s.Children.Add(v); s.Children.Add(Label(hint, 11, "#777589")); b.Child = s; return b; }
    private Button ActionButton(string text) { var button = new Button { Content = text, Style = (Style)Application.Current.Resources["SecondaryButton"], Margin = new(7, 0, 0, 0), Padding = new(13, 8, 13, 8), ToolTip = $"{text} this PC", Tag = text }; button.Click += QuickAction_Click; return button; }
    private static Border Empty(string body, string title) { var b = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 24, 33)), CornerRadius = new(10), Padding = new(20), Margin = new(0, 14, 0, 0) }; var s = Stack(); s.Children.Add(Label(title, 14)); s.Children.Add(Body(body)); b.Child = s; return b; }
    private static FrameworkElement Row(string name, string value) { var g = new Grid { Margin = new(0, 0, 0, 12) }; g.ColumnDefinitions.Add(new() { Width = new(180) }); g.ColumnDefinitions.Add(new()); g.Children.Add(Label(name, 13, "#A5A3B5")); var v = Label(value, 13); Grid.SetColumn(v, 1); g.Children.Add(v); return g; }
    private static FrameworkElement Section(string name, string content, string status) { var b = Card(); var g = new Grid(); g.ColumnDefinitions.Add(new() { Width = new(180) }); g.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); g.ColumnDefinitions.Add(new() { Width = new(240) }); g.Children.Add(Label(name, 15)); var c = Body(content); c.Margin = new(0); Grid.SetColumn(c, 1); g.Children.Add(c); var st = Label(status, 12, "#A5A3B5"); st.TextAlignment = TextAlignment.Right; Grid.SetColumn(st, 2); g.Children.Add(st); b.Child = g; return b; }
    private static FrameworkElement ButtonBar(params string[] labels) { var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) }; foreach (var label in labels) s.Children.Add(new Button { Content = label, Style = (Style)Application.Current.Resources[label == labels[0] ? "PrimaryButton" : "SecondaryButton"], Margin = new(0, 0, 9, 0) }); return s; }
    private static FrameworkElement Page(string title, string subtitle, params FrameworkElement[] children) { var s = Stack(); s.Children.Add(Heading(title, subtitle)); foreach (var child in children) { if (child is not Border { Style: not null }) { var card = Card(); var inner = Stack(); inner.Children.Add(child); card.Child = inner; s.Children.Add(card); } else s.Children.Add(child); } return s; }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) { var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent", "logs"); System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        var action = (string)((Button)sender).Tag;
        var command = action switch { "Lock" => "system.lock", "Sleep" => "system.sleep", "Restart" => "system.restart", _ => "system.shutdown" };
        if (!_settings.EnabledControls.GetValueOrDefault(command)) { MessageBox.Show($"{action} is disabled in local PC Bridge settings.", "Control disabled", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (action is "Restart" or "Shut down" && MessageBox.Show($"Are you sure you want to {action.ToLowerInvariant()} this PC?", "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_demo) { MessageBox.Show($"Demo: {action} command accepted.", "PC Bridge Agent"); return; }
        var result = await new PowerCommandHandler().ExecuteAsync(command, null, CancellationToken.None);
        if (!result.Success) MessageBox.Show(result.Message, "Command failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
