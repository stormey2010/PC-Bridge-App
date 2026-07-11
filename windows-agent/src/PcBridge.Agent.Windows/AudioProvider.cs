using System.Text.Json;
using NAudio.CoreAudioApi;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class AudioProvider(TimeSpan interval) : ISensorProvider
{
    // Poll slower than CPU — helper launches are expensive; cache covers the gap.
    public string Name => "Audio";
    public TimeSpan Interval => interval > TimeSpan.FromSeconds(30) ? interval : TimeSpan.FromSeconds(30);
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
    private static readonly object Gate = new();
    private static DateTimeOffset _cacheUntil = DateTimeOffset.MinValue;
    private static double _cachedVolume;
    private static bool _cachedMuted;
    private static string _cachedDevice = "Unknown";
    private static bool _hasCache;

    public static bool TryRead(out double volume, out bool muted, out string deviceName)
    {
        lock (Gate)
        {
            if (_hasCache && DateTimeOffset.UtcNow < _cacheUntil)
            {
                volume = _cachedVolume;
                muted = _cachedMuted;
                deviceName = _cachedDevice;
                return true;
            }
        }

        if (!InteractiveProcess.IsServiceSession())
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                volume = Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                muted = device.AudioEndpointVolume.Mute;
                deviceName = device.FriendlyName;
                StoreCache(volume, muted, deviceName);
                return true;
            }
            catch
            {
                volume = 0; muted = false; deviceName = "Unknown";
                return false;
            }
        }

        var helper = InteractiveProcess.FindSessionHelper();
        if (helper is null)
        {
            volume = 0; muted = false; deviceName = "Unknown";
            return false;
        }
        var outFile = Path.Combine(Path.GetTempPath(), $"pcbridge-audio-{Guid.NewGuid():N}.json");
        try
        {
            var result = InteractiveProcess.Run("Audio read", helper, "volume-get", TimeSpan.FromSeconds(10), outFile, hidden: true);
            if (!result.Success || !File.Exists(outFile))
            {
                volume = 0; muted = false; deviceName = "Unknown";
                return false;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                volume = 0; muted = false; deviceName = "Unknown";
                return false;
            }
            volume = root.GetProperty("volume").GetDouble();
            muted = root.GetProperty("muted").GetBoolean();
            deviceName = root.TryGetProperty("device", out var d) ? d.GetString() ?? "Unknown" : "Unknown";
            StoreCache(volume, muted, deviceName);
            return true;
        }
        catch
        {
            volume = 0; muted = false; deviceName = "Unknown";
            return false;
        }
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
                StoreCache(volume, device.AudioEndpointVolume.Mute, device.FriendlyName);
                return new(true, null, $"Volume changed to {volume:0}%.");
            }
            catch { return new(false, "windows_error", "Windows could not change the volume."); }
        }
        var result = RunHelper($"volume-set {volume:0.###}", $"Volume changed to {volume:0}%.");
        if (result.Success) StoreCache(volume, _cachedMuted, _cachedDevice);
        return result;
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
                StoreCache(Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), muted, device.FriendlyName);
                return new(true, null, muted ? "Audio muted." : "Audio unmuted.");
            }
            catch { return new(false, "windows_error", "Windows could not change mute."); }
        }
        var result = RunHelper($"mute-set {muted.ToString().ToLowerInvariant()}", muted ? "Audio muted." : "Audio unmuted.");
        if (result.Success) StoreCache(_cachedVolume, muted, _cachedDevice);
        return result;
    }

    private static void StoreCache(double volume, bool muted, string deviceName)
    {
        lock (Gate)
        {
            _cachedVolume = volume;
            _cachedMuted = muted;
            _cachedDevice = deviceName;
            _hasCache = true;
            _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(25);
        }
    }

    private static CommandResult RunHelper(string args, string successMessage)
    {
        var helper = InteractiveProcess.FindSessionHelper();
        if (helper is null) return new(false, "missing_helper", "Session helper is not installed. Reinstall PC Bridge Agent.");
        var outFile = Path.Combine(Path.GetTempPath(), $"pcbridge-audio-{Guid.NewGuid():N}.json");
        try
        {
            var result = InteractiveProcess.Run("Audio control", helper, args, TimeSpan.FromSeconds(15), outFile, hidden: true);
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
