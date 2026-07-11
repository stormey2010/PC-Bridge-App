using NAudio.CoreAudioApi;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class AudioProvider(TimeSpan interval) : ISensorProvider
{
    private readonly MMDeviceEnumerator _enumerator = new();
    public string Name => "Audio"; public TimeSpan Interval => interval;
    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("volume_level", "sensor", "Volume", null, "%"),
        new("volume", "number", "Volume", null, "%", Command: "audio.set_volume"),
        new("mute", "switch", "Mute", Command: "audio.set_mute"),
        new("default_output_device", "sensor", "Default output device", null, null, false, "diagnostic")
    ];
    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return Task.FromResult<IReadOnlyList<EntityState>>([
            new("volume_level", Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100)),
            new("volume", Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100)),
            new("mute", device.AudioEndpointVolume.Mute),
            new("default_output_device", device.FriendlyName)
        ]);
    }
    public ValueTask DisposeAsync() { _enumerator.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class AudioCommandHandler : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio.set_volume", "audio.set_mute" };
    public Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (command == "audio.set_volume")
        {
            if (parameters is null || !parameters.TryGetValue("volume", out var value) || !value.TryGetDouble(out var volume) || volume is < 0 or > 100)
                return Task.FromResult(new CommandResult(false, "invalid_parameters", "Volume must be between 0 and 100."));
            device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(volume / 100);
            return Task.FromResult(new CommandResult(true, null, $"Volume changed to {volume:0}%."));
        }
        if (parameters is null || !parameters.TryGetValue("muted", out var muted) || muted.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            return Task.FromResult(new CommandResult(false, "invalid_parameters", "Muted must be true or false."));
        device.AudioEndpointVolume.Mute = muted.GetBoolean();
        return Task.FromResult(new CommandResult(true, null, muted.GetBoolean() ? "Audio muted." : "Audio unmuted."));
    }
}
