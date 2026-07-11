using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcBridge.Agent.App;

public sealed record UpdateInfo(string CurrentVersion, string LatestVersion, string ReleaseUrl, string? InstallerUrl, string? ReleaseNotes)
{
    public bool IsUpdateAvailable => CompareVersions(LatestVersion, CurrentVersion) > 0;

    public static int CompareVersions(string left, string right)
    {
        static Version Parse(string value)
        {
            var cleaned = Regex.Replace(value.Trim().TrimStart('v', 'V'), @"[^0-9.]+.*$", "");
            return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0, 0);
        }
        return Parse(left).CompareTo(Parse(right));
    }
}

public static class UpdateChecker
{
    public const string AppReleasesUrl = "https://github.com/stormey2010/PC-Bridge-App/releases/latest";
    public const string HaReleasesUrl = "https://github.com/stormey2010/PC-Bridge-HA/releases/latest";
    public const string HaHacsHint = "In Home Assistant open HACS → Integrations → PC Bridge → Download / Update, then restart Home Assistant.";

    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync("https://api.github.com/repos/stormey2010/PC-Bridge-App/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? CurrentVersion;
        var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? AppReleasesUrl : AppReleasesUrl;
        var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
        string? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                installer = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        return new UpdateInfo(CurrentVersion, tag, htmlUrl, installer, notes);
    }

    public static async Task<string> DownloadInstallerAsync(string installerUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCBridgeUpdates");
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = $"PC-Bridge-Agent-{DateTime.UtcNow:yyyyMMddHHmmss}-setup.exe";
        var path = Path.Combine(directory, fileName);

        using var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (total > 0) progress?.Report(readTotal * 100.0 / total);
        }
        return path!;
    }

    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCBridgeAgent", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
