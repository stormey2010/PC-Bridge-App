using System.Windows;
using PcBridge.Agent.Core;
using PcBridge.Agent.Windows;

namespace PcBridge.Agent.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase)
            || (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.EndsWith(".Demo", StringComparison.OrdinalIgnoreCase) ?? false);
#if DEBUG
        demo = true;
#endif
        var settingsStore = new SettingsStore();
        var credentials = new DpapiCredentialStore();
        var settings = await settingsStore.LoadAsync();
        if (demo)
            settings = new AgentSettings { DeviceName = "PARKER-DESKTOP", HomeAssistantUrl = "https://home.example.com" };
        else if (string.IsNullOrWhiteSpace(settings.HomeAssistantUrl) || await credentials.GetTokenAsync() is null)
        {
            var setup = new SetupWindow(settingsStore, credentials, settings);
            if (setup.ShowDialog() != true) { Shutdown(); return; }
            settings = await settingsStore.LoadAsync();
        }
        new MainWindow(settings, settingsStore, credentials, demo).Show();
    }
}
