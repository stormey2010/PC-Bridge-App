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
    KeepAwakeController keepAwakeController,
    IEnumerable<ICommandHandler> commandHandlers) : BackgroundService
{
    private readonly SemaphoreSlim _reconnectSignal = new(0, 1);
    private readonly DuplicateCommandGuard _duplicates = new(TimeSpan.FromHours(1));
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
                var updateInterval = TimeSpan.FromSeconds(Math.Max(2, _settings.FastUpdateSeconds));
                var providers = new List<ISensorProvider>();
                if (_settings.EnabledSensorGroups.GetValueOrDefault("system", true)) providers.Add(new SystemProvider(updateInterval));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("audio", true)) providers.Add(new AudioProvider(updateInterval));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("network", true)) providers.Add(new NetworkProvider(updateInterval));
                if (_settings.EnabledSensorGroups.GetValueOrDefault("keep_awake", true)) providers.Add(new KeepAwakeProvider(keepAwakeController, updateInterval));
                _providers = providers;

                var device = DeviceInformation.Read();
                var entities = _providers.SelectMany(p => p.Describe()).Concat([
                    new EntityDescriptor("lock", "button", "Lock", null, null),
                    new EntityDescriptor("sleep", "button", "Sleep", null, null),
                    new EntityDescriptor("restart", "button", "Restart", null, null, false),
                    new EntityDescriptor("shutdown", "button", "Shut down", null, null, false)
                ]).ToArray();
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
                    // Settings/network wake requested a clean reconnect.
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
        do
        {
            try
            {
                var states = await provider.ReadAsync(cancellationToken);
                await connection.SendStatesAsync(_settings.InstallationId, states, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Sensor provider {Provider} failed without stopping other providers", provider.Name); }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task HandleCommandAsync(string messageId, CommandRequest request)
    {
        CommandResult result;
        if (!_duplicates.TryAccept(messageId, DateTimeOffset.UtcNow)) result = new(true, "duplicate", "Duplicate command was not executed again.");
        else if (!_settings.EnabledControls.GetValueOrDefault(request.Command)) result = new(false, "disabled", "This control is disabled in PC Bridge Agent settings.");
        else if (!_commands.TryGetValue(request.Command, out var handler)) result = new(false, "unknown_command", "The agent rejected an unknown command type.");
        else
        {
            logger.LogInformation("Executing approved command {Command} with request {RequestId}", request.Command, messageId);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
