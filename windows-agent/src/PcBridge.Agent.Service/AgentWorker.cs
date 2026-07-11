using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcBridge.Agent.Core;
using PcBridge.Agent.Windows;

namespace PcBridge.Agent.Service;

public sealed class AgentWorker(
    ILogger<AgentWorker> logger,
    SettingsStore settingsStore,
    ICredentialStore credentialStore,
    IConnectionStatusStore statusStore,
    HomeAssistantConnection connection,
    IEnumerable<ICommandHandler> commandHandlers) : BackgroundService
{
    private readonly SemaphoreSlim _reconnectSignal = new(0, 1);
    private readonly DuplicateCommandGuard _duplicates = new(TimeSpan.FromHours(1));
    private readonly StateChangeTracker _changes = new();
    private AgentSettings _settings = new();
    private IReadOnlyList<ISensorProvider> _providers = [];
    private Dictionary<string, ICommandHandler> _commands = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _session;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        connection.CommandReceived += HandleCommandAsync;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        using var settingsWatcher = CreateSettingsWatcher();
        _commands = commandHandlers.SelectMany(handler => handler.Commands.Select(command => (command, handler))).ToDictionary(item => item.command, item => item.handler, StringComparer.OrdinalIgnoreCase);
        var failures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _settings = await settingsStore.LoadAsync(stoppingToken);
                var token = await credentialStore.GetTokenAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(_settings.HomeAssistantUrl) || string.IsNullOrWhiteSpace(token))
                {
                    statusStore.Set(new(ConnectionStatus.Disconnected, statusStore.Current.LastConnected, "Agent is not configured. Open PC Bridge Agent to complete setup."));
                    logger.LogWarning("Agent is not configured. Open PC Bridge Agent to complete setup.");
                    try { await _reconnectSignal.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { break; }
                    continue;
                }
                if (!Uri.TryCreate(_settings.HomeAssistantUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    statusStore.Set(new(ConnectionStatus.Disconnected, statusStore.Current.LastConnected, "Configured Home Assistant URL is invalid."));
                    logger.LogError("Configured Home Assistant URL is invalid");
                    try { await _reconnectSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
                    continue;
                }

                await DisposeProvidersAsync();
                _changes.Reset();
                var fast = TimeSpan.FromSeconds(Math.Max(5, _settings.FastUpdateSeconds));
                var slow = TimeSpan.FromSeconds(Math.Max(30, _settings.StaticUpdateSeconds));
                var providers = new List<ISensorProvider>();
                if (_settings.EnabledSensorGroups.GetValueOrDefault("system", true)) providers.Add(new SystemProvider(fast));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("audio", true)) providers.Add(new AudioProvider(fast));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("network", true)) providers.Add(new NetworkProvider(fast));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("storage", true)) providers.Add(new StorageProvider(slow));
                _providers = providers;

                var device = DeviceInformation.Read();
                var entities = _providers.SelectMany(p => p.Describe())
                    .Concat(BuildControlButtons(_settings))
                    .Concat(_settings.EnabledControls.GetValueOrDefault("app.launch")
                        ? ApplicationCommandHandler.Describe(_settings.AllowedApplications)
                        : [])
                    .Concat(_settings.EnabledControls.GetValueOrDefault("custom.run")
                        ? CustomCommandHandler.Describe(_settings.CustomCommands)
                        : [])
                    .ToArray();
                var registration = new AgentRegistration(_settings.InstallationId, _settings.DeviceName, typeof(AgentWorker).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", device.Manufacturer, device.Model, Environment.OSVersion.VersionString, entities);

                using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _session = session;
                try
                {
                    await connection.ConnectAsync(uri, token, registration, session.Token);
                    failures = 0;
                    var readers = _providers.Select(provider => RunProviderAsync(provider, session.Token)).ToArray();
                    await connection.ListenAsync(session.Token);
                    session.Cancel();
                    await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException)
                {
                    statusStore.Set(new(ConnectionStatus.Disconnected, statusStore.Current.LastConnected, "Reconnecting to Home Assistant…"));
                }
                catch (Exception ex)
                {
                    failures++;
                    if (statusStore.Current.Status is not (ConnectionStatus.AuthenticationFailed or ConnectionStatus.Incompatible))
                        statusStore.Set(new(ConnectionStatus.Disconnected, statusStore.Current.LastConnected, "Home Assistant is currently unreachable. PC Bridge will retry automatically."));
                    logger.LogWarning(ex, "Connection ended; retry attempt {Attempt}", failures);
                }
                finally { _session = null; }

                var seconds = Math.Min(300, Math.Pow(2, Math.Min(failures, 8))) + Random.Shared.NextDouble();
                try { await _reconnectSignal.WaitAsync(TimeSpan.FromSeconds(seconds), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            await DisposeProvidersAsync();
            statusStore.Set(new(ConnectionStatus.Disconnected, statusStore.Current.LastConnected, "Agent service stopped."));
        }
    }

    private static IEnumerable<EntityDescriptor> BuildControlButtons(AgentSettings settings)
    {
        // Anything the user enabled locally is registered as enabled in HA (no greyed-out entities).
        foreach (var (key, name, command) in new (string Key, string Name, string Command)[]
        {
            ("lock", "Lock", "system.lock"),
            ("sleep", "Sleep", "system.sleep"),
            ("hibernate", "Hibernate", "system.hibernate"),
            ("logoff", "Log off", "system.logoff"),
            ("restart", "Restart", "system.restart"),
            ("shutdown", "Shut down", "system.shutdown"),
            ("display_off", "Turn display off", "system.display_off"),
            ("abort_shutdown", "Cancel shutdown", "system.abort_shutdown"),
            ("open_explorer", "Open File Explorer", "system.open_explorer"),
            ("open_settings", "Open Settings", "system.open_settings")
        })
        {
            if (!settings.EnabledControls.GetValueOrDefault(command)) continue;
            yield return new(key, "button", name, EnabledByDefault: true, Command: command);
        }
    }

    private FileSystemWatcher? CreateSettingsWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsStore.SettingsPath);
            if (string.IsNullOrWhiteSpace(directory)) return null;
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory)
            {
                Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            void OnChanged(object sender, FileSystemEventArgs args)
            {
                var name = Path.GetFileName(args.Name ?? args.FullPath);
                if (!name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("credential.bin", StringComparison.OrdinalIgnoreCase))
                    return;
                logger.LogInformation("Configuration changed on disk; reconnecting agent");
                RequestReconnect();
            }
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += (_, args) => OnChanged(_, new FileSystemEventArgs(WatcherChangeTypes.Changed, directory, args.Name));
            return watcher;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not watch settings for automatic reconnect");
            return null;
        }
    }

    private void RequestReconnect()
    {
        try { _session?.Cancel(); } catch { /* ignore */ }
        if (_reconnectSignal.CurrentCount == 0) _reconnectSignal.Release();
    }

    private async Task DisposeProvidersAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
        _providers = [];
    }

    private async Task RunProviderAsync(ISensorProvider provider, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(provider.Interval);
        var seedTracker = true;
        do
        {
            try
            {
                var states = await provider.ReadAsync(cancellationToken);
                IReadOnlyList<EntityState> payload;
                if (seedTracker)
                {
                    _ = _changes.FilterChanged(states);
                    payload = states;
                    seedTracker = false;
                }
                else
                {
                    payload = _changes.FilterChanged(states);
                }
                if (payload.Count == 0) continue;
                await connection.SendStatesAsync(_settings.InstallationId, payload, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Sensor provider {Provider} failed without stopping other providers", provider.Name); }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task HandleCommandAsync(string messageId, CommandRequest request)
    {
        try { _settings = await settingsStore.LoadAsync(); } catch { /* keep cached settings */ }

        CommandResult result;
        if (!_duplicates.TryAccept(messageId, DateTimeOffset.UtcNow)) result = new(true, "duplicate", "Duplicate command was not executed again.");
        else if (!_settings.EnabledControls.GetValueOrDefault(request.Command)) result = new(false, "disabled", "This control is disabled in PC Bridge Agent settings.");
        else if (!_commands.TryGetValue(request.Command, out var handler)) result = new(false, "unknown_command", "The agent rejected an unknown command type.");
        else
        {
            logger.LogInformation("Executing approved command {Command} with request {RequestId}", request.Command, messageId);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                result = await handler.ExecuteAsync(request.Command, request.Parameters, timeout.Token);
            }
            catch (OperationCanceledException) { result = new(false, "timeout", "The command timed out."); }
            catch (Exception ex) { logger.LogError(ex, "Command {Command} failed", request.Command); result = new(false, "execution_failed", "The command failed on the PC."); }
        }
        try { await connection.SendCommandResultAsync(_settings.InstallationId, messageId, result, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not send command result {RequestId}", messageId); }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs args) { if (args.IsAvailable) RequestReconnect(); }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args) { if (args.Mode == PowerModes.Resume) RequestReconnect(); }
}
