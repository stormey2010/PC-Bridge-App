using System.Text.Json;
using NAudio.CoreAudioApi;

// Tiny helper that runs in the interactive user session so volume/mute hit the real speakers.
if (args.Length == 0) return Fail("missing_command");

var command = args[0].ToLowerInvariant();
string? outPath = null;
var values = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i] is "--out" && i + 1 < args.Length) { outPath = args[++i]; continue; }
    values.Add(args[i]);
}

try
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    switch (command)
    {
        case "volume-get":
            return Write(outPath, new { ok = true, volume = Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), muted = device.AudioEndpointVolume.Mute, device = device.FriendlyName });
        case "volume-set":
            if (values.Count == 0 || !double.TryParse(values[0], out var volume) || volume is < 0 or > 100) return Fail("invalid_volume");
            device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(volume / 100.0);
            return Write(outPath, new { ok = true, volume });
        case "mute-set":
            if (values.Count == 0 || !bool.TryParse(values[0], out var muted)) return Fail("invalid_mute");
            device.AudioEndpointVolume.Mute = muted;
            return Write(outPath, new { ok = true, muted });
        case "mute-get":
            return Write(outPath, new { ok = true, muted = device.AudioEndpointVolume.Mute, volume = Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100) });
        default:
            return Fail("unknown_command");
    }
}
catch (Exception ex)
{
    return Write(outPath, new { ok = false, error = ex.GetType().Name });
}

static int Write(string? path, object payload)
{
    var json = JsonSerializer.Serialize(payload);
    if (!string.IsNullOrWhiteSpace(path))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
    else Console.WriteLine(json);
    return 0;
}

static int Fail(string code)
{
    Console.Error.WriteLine(code);
    return 1;
}
