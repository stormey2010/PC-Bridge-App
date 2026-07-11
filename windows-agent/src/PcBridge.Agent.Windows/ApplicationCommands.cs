using System.Text.RegularExpressions;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class ApplicationCommandHandler(SettingsStore settingsStore) : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app.launch" };

    public async Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null || !parameters.TryGetValue("app_id", out var idElement) || idElement.ValueKind != System.Text.Json.JsonValueKind.String)
            return new(false, "invalid_parameters", "app_id is required.");
        var appId = idElement.GetString() ?? string.Empty;
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var app = settings.AllowedApplications.FirstOrDefault(item => item.Id.Equals(appId, StringComparison.OrdinalIgnoreCase) && item.Enabled);
        if (app is null) return new(false, "not_allowlisted", "That application is not on the local allowlist.");
        return InteractiveProcess.Launch(app.Name, app.ExecutablePath, app.Arguments, requiresElevation: false);
    }

    public static IReadOnlyList<EntityDescriptor> Describe(IEnumerable<AllowedApplication> apps) =>
        apps.Where(app => app.Enabled && !string.IsNullOrWhiteSpace(app.Name) && !string.IsNullOrWhiteSpace(app.ExecutablePath))
            .Select(app => new EntityDescriptor(
                $"app_{Sanitize(app.Id)}",
                "button",
                app.Name,
                Command: "app.launch",
                CommandParameters: new Dictionary<string, object?> { ["app_id"] = app.Id }))
            .ToArray();

    private static string Sanitize(string value) => Regex.Replace(value, @"[^a-zA-Z0-9_]+", "_").Trim('_').ToLowerInvariant();
}

public sealed class CustomCommandHandler(SettingsStore settingsStore) : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom.run" };

    public async Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null || !parameters.TryGetValue("command_id", out var idElement) || idElement.ValueKind != System.Text.Json.JsonValueKind.String)
            return new(false, "invalid_parameters", "command_id is required.");
        var commandId = idElement.GetString() ?? string.Empty;
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var item = settings.CustomCommands.FirstOrDefault(entry => entry.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase) && entry.Enabled);
        if (item is null) return new(false, "not_allowlisted", "That custom command is not configured on this PC.");
        // HA cannot supply the path or arguments — only a pre-approved local command id.
        return InteractiveProcess.Launch(item.Name, item.ExecutablePath, item.Arguments, item.RequiresElevation);
    }

    public static IReadOnlyList<EntityDescriptor> Describe(IEnumerable<CustomCommand> commands) =>
        commands.Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.ExecutablePath))
            .Select(item => new EntityDescriptor(
                $"cmd_{Sanitize(item.Id)}",
                "button",
                item.Name,
                EnabledByDefault: false,
                Command: "custom.run",
                CommandParameters: new Dictionary<string, object?> { ["command_id"] = item.Id }))
            .ToArray();

    private static string Sanitize(string value) => Regex.Replace(value, @"[^a-zA-Z0-9_]+", "_").Trim('_').ToLowerInvariant();
}
