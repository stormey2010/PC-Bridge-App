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
    public int FastUpdateSeconds { get; set; } = 5;
    public int StaticUpdateSeconds { get; set; } = 60;
    public int UnavailableTimeoutSeconds { get; set; } = 30;
    public Dictionary<string, bool> EnabledSensorGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = true,
        ["audio"] = true,
        ["network"] = true,
        ["keep_awake"] = true
    };
    public Dictionary<string, bool> EnabledControls { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system.lock"] = true,
        ["system.sleep"] = true,
        ["system.restart"] = false,
        ["system.shutdown"] = false,
        ["keep_awake.set"] = true,
        ["audio.set_volume"] = true,
        ["audio.set_mute"] = true
    };
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
        return await JsonSerializer.DeserializeAsync<AgentSettings>(stream, Protocol.Json, cancellationToken)
            ?? new AgentSettings();
    }

    public async Task SaveAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var temp = SettingsPath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, settings, Protocol.Json, cancellationToken);
        File.Move(temp, SettingsPath, true);
    }
}
