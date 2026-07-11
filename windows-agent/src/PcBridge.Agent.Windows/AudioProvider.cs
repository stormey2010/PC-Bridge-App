using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class AudioProvider(TimeSpan interval) : ISensorProvider
{
    public string Name => "Audio";
    public TimeSpan Interval => interval;
    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("volume_level", "sensor", "Volume", null, "%"),
        new("volume", "number", "Volume", null, "%", Command: "audio.set_volume"),
        new("mute", "switch", "Mute", Command: "audio.set_mute"),
        new("default_output_device", "sensor", "Default output device", null, null, false, "diagnostic")
    ];

    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        if (SessionAudio.TryRead(out var volume, out var muted, out var deviceName))
        {
            return Task.FromResult<IReadOnlyList<EntityState>>([
                new("volume_level", volume),
                new("volume", volume),
                new("mute", muted),
                new("default_output_device", deviceName)
            ]);
        }
        return Task.FromResult<IReadOnlyList<EntityState>>([
            new("volume_level", null),
            new("volume", null),
            new("mute", null),
            new("default_output_device", "Unavailable")
        ]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class AudioCommandHandler : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio.set_volume", "audio.set_mute" };

    public Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, JsonElement>? parameters, CancellationToken cancellationToken)
    {
        if (command == "audio.set_volume")
        {
            if (parameters is null || !parameters.TryGetValue("volume", out var value) || !value.TryGetDouble(out var volume) || volume is < 0 or > 100)
                return Task.FromResult(new CommandResult(false, "invalid_parameters", "Volume must be between 0 and 100."));
            return Task.FromResult(SessionAudio.SetVolume(volume));
        }
        if (parameters is null || !parameters.TryGetValue("muted", out var muted) || muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Task.FromResult(new CommandResult(false, "invalid_parameters", "Muted must be true or false."));
        return Task.FromResult(SessionAudio.SetMute(muted.GetBoolean()));
    }
}

internal static class SessionAudio
{
    public static bool TryRead(out double volume, out bool muted, out string deviceName)
    {
        volume = 0; muted = false; deviceName = "Unknown";
        if (!InteractiveProcess.IsServiceSession())
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                volume = Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                muted = device.AudioEndpointVolume.Mute;
                deviceName = device.FriendlyName;
                return true;
            }
            catch { return false; }
        }

        var helper = InteractiveProcess.FindSessionHelper();
        if (helper is null) return false;
        var outFile = Path.Combine(Path.GetTempPath(), $"pcbridge-audio-{Guid.NewGuid():N}.json");
        try
        {
            var result = InteractiveProcess.Run("Audio read", helper, "volume-get", TimeSpan.FromSeconds(10), outFile);
            if (!result.Success || !File.Exists(outFile)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return false;
            volume = root.GetProperty("volume").GetDouble();
            muted = root.GetProperty("muted").GetBoolean();
            deviceName = root.TryGetProperty("device", out var d) ? d.GetString() ?? "Unknown" : "Unknown";
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(outFile); } catch { /* ignore */ } }
    }

    public static CommandResult SetVolume(double volume)
    {
        if (!InteractiveProcess.IsServiceSession())
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(volume / 100);
                return new(true, null, $"Volume changed to {volume:0}%.");
            }
            catch { return new(false, "windows_error", "Windows could not change the volume."); }
        }
        return RunHelper($"volume-set {volume:0.###}", $"Volume changed to {volume:0}%.");
    }

    public static CommandResult SetMute(bool muted)
    {
        if (!InteractiveProcess.IsServiceSession())
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = muted;
                return new(true, null, muted ? "Audio muted." : "Audio unmuted.");
            }
            catch { return new(false, "windows_error", "Windows could not change mute."); }
        }
        return RunHelper($"mute-set {muted.ToString().ToLowerInvariant()}", muted ? "Audio muted." : "Audio unmuted.");
    }

    private static CommandResult RunHelper(string args, string successMessage)
    {
        var helper = InteractiveProcess.FindSessionHelper();
        if (helper is null) return new(false, "missing_helper", "Session helper is not installed. Reinstall PC Bridge Agent.");
        var outFile = Path.Combine(Path.GetTempPath(), $"pcbridge-audio-{Guid.NewGuid():N}.json");
        try
        {
            var result = InteractiveProcess.Run("Audio control", helper, args, TimeSpan.FromSeconds(15), outFile);
            if (!result.Success) return result;
            if (File.Exists(outFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    return new(true, null, successMessage);
            }
            return new(false, "windows_error", "Audio control did not complete in the user session.");
        }
        finally { try { File.Delete(outFile); } catch { /* ignore */ } }
    }
}
