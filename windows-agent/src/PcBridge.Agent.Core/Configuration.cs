using System.Text.Json;

namespace PcBridge.Agent.Core;

public sealed class AgentSettings
{
    public string InstallationId { get; set; } = Guid.NewGuid().ToString("D");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string HomeAssistantUrl { get; set; } = string.Empty;
    public string? Area { get; set; }
    public bool StartAutomatically { get; set; } = true;
    public bool PrivacySensorsEnabled { get; set; }
    /// <summary>How often fast-changing sensors (CPU, memory, audio, network rates) are sampled.</summary>
    public int FastUpdateSeconds { get; set; } = 10;
    /// <summary>How often slow-changing sensors (keep awake, disk, IP, boot time) are sampled.</summary>
    public int StaticUpdateSeconds { get; set; } = 60;
    public int UnavailableTimeoutSeconds { get; set; } = 30;
    public Dictionary<string, bool> EnabledSensorGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = true,
        ["audio"] = true,
        ["network"] = true,
        ["keep_awake"] = true,
        ["storage"] = true
    };
    public Dictionary<string, bool> EnabledControls { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system.lock"] = true,
        ["system.sleep"] = true,
        ["system.hibernate"] = false,
        ["system.logoff"] = false,
        ["system.restart"] = false,
        ["system.shutdown"] = false,
        ["keep_awake.set"] = true,
        ["audio.set_volume"] = true,
        ["audio.set_mute"] = true,
        ["app.launch"] = true,
        ["custom.run"] = false
    };
    public List<AllowedApplication> AllowedApplications { get; set; } = [];
    public List<CustomCommand> CustomCommands { get; set; } = [];
}

public sealed class AllowedApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class CustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool RequiresElevation { get; set; }
    public bool Enabled { get; set; } = true;
}

public interface ICredentialStore
{
    Task SaveTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    Task RemoveTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsStore(string? baseDirectory = null)
{
    private readonly string _directory = baseDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent");
    public string SettingsPath => Path.Combine(_directory, "settings.json");

    public async Task<AgentSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath)) return new AgentSettings();
        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AgentSettings>(stream, Protocol.Json, cancellationToken)
            ?? new AgentSettings();
        EnsureControlDefaults(settings);
        return settings;
    }

    public async Task SaveAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureControlDefaults(settings);
        Directory.CreateDirectory(_directory);
        var temp = SettingsPath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, settings, Protocol.Json, cancellationToken);
        File.Move(temp, SettingsPath, true);
    }

    private static void EnsureControlDefaults(AgentSettings settings)
    {
        foreach (var (key, value) in new AgentSettings().EnabledControls)
            settings.EnabledControls.TryAdd(key, value);
        foreach (var (key, value) in new AgentSettings().EnabledSensorGroups)
            settings.EnabledSensorGroups.TryAdd(key, value);
        settings.FastUpdateSeconds = Math.Clamp(settings.FastUpdateSeconds, 5, 300);
        settings.StaticUpdateSeconds = Math.Clamp(settings.StaticUpdateSeconds, 30, 3600);
    }
}
