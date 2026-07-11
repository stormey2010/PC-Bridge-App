using System.Diagnostics;
using System.Runtime.InteropServices;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class KeepAwakeController : ICommandHandler, IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private IntPtr _powerRequest;
    public bool IsEnabled { get; private set; }
    public bool KeepDisplayOn { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keep_awake.set" };

    public Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null || !parameters.TryGetValue("enabled", out var enabledElement) || enabledElement.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            return Task.FromResult(new CommandResult(false, "invalid_parameters", "Enabled must be true or false."));
        var enabled = enabledElement.GetBoolean();
        var display = parameters.TryGetValue("display", out var displayElement) && displayElement.ValueKind == System.Text.Json.JsonValueKind.True;
        int? minutes = parameters.TryGetValue("minutes", out var minutesElement) && minutesElement.TryGetInt32(out var parsed) ? parsed : null;
        if (minutes is < 1 or > 1440) return Task.FromResult(new CommandResult(false, "invalid_parameters", "Duration must be between 1 and 1440 minutes."));
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            if (!enabled) Release();
            else
            {
                var reason = Marshal.StringToHGlobalUni("PC Bridge keep-awake control");
                try
                {
                    var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
                    _powerRequest = PowerCreateRequest(ref context);
                }
                finally { Marshal.FreeHGlobal(reason); }
                if (_powerRequest == IntPtr.Zero || _powerRequest == new IntPtr(-1) || !PowerSetRequest(_powerRequest, PowerRequestType.SystemRequired) || (display && !PowerSetRequest(_powerRequest, PowerRequestType.DisplayRequired)))
                {
                    Release();
                    return Task.FromResult(new CommandResult(false, "windows_error", "Windows rejected the keep-awake request."));
                }
                IsEnabled = true; KeepDisplayOn = display; ExpiresAt = minutes is null ? null : DateTimeOffset.UtcNow.AddMinutes(minutes.Value);
                if (minutes is not null) _timer = new Timer(_ => { lock (_gate) Release(); }, null, TimeSpan.FromMinutes(minutes.Value), Timeout.InfiniteTimeSpan);
            }
        }
        return Task.FromResult(new CommandResult(true, null, enabled ? "Keep awake enabled." : "Keep awake disabled."));
    }

    private void Release()
    {
        if (_powerRequest != IntPtr.Zero && _powerRequest != new IntPtr(-1))
        {
            PowerClearRequest(_powerRequest, PowerRequestType.SystemRequired);
            PowerClearRequest(_powerRequest, PowerRequestType.DisplayRequired);
            CloseHandle(_powerRequest);
            _powerRequest = IntPtr.Zero;
        }
        IsEnabled = false; KeepDisplayOn = false; ExpiresAt = null;
    }
    public void Dispose() { lock (_gate) { _timer?.Dispose(); Release(); } }
    private enum PowerRequestType { DisplayRequired = 0, SystemRequired = 1, AwayModeRequired = 2, ExecutionRequired = 3 }
    [StructLayout(LayoutKind.Sequential)] private struct ReasonContext { public uint Version; public uint Flags; public IntPtr SimpleReasonString; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class KeepAwakeProvider(KeepAwakeController controller, TimeSpan interval) : ISensorProvider
{
    public string Name => "Keep awake"; public TimeSpan Interval => interval;
    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("keep_awake", "switch", "Keep awake"),
        new("keep_awake_reason", "sensor", "Keep awake reason"),
        new("keep_awake_remaining", "sensor", "Keep awake remaining", "duration", "s")
    ];
    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        double? remaining = controller.ExpiresAt is null ? null : Math.Max(0, (controller.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
        return Task.FromResult<IReadOnlyList<EntityState>>([new("keep_awake", controller.IsEnabled), new("keep_awake_reason", controller.IsEnabled ? (controller.KeepDisplayOn ? "System and display" : "System") : "Disabled"), new("keep_awake_remaining", remaining is null ? null : Math.Round(remaining.Value))]);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PowerCommandHandler : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "system.lock", "system.sleep", "system.restart", "system.shutdown" };
    public async Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        if (command == "system.lock")
            return LockWorkStation() ? new(true, null, "PC locked successfully.") : new(false, "windows_error", "Windows rejected the lock request.");
        if (command == "system.sleep")
            return SetSuspendState(false, false, false) ? new(true, null, "Sleep request accepted.") : new(false, "windows_error", "Windows rejected the sleep request.");
        var arguments = command == "system.restart" ? "/r /t 0" : "/s /t 0";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return new(false, "start_failed", "Windows could not start the shutdown request.");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? new(true, null, command == "system.restart" ? "Restart accepted by Windows." : "Shutdown accepted by Windows.") : new(false, "windows_error", $"Windows rejected the request with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, "windows_error", "Windows could not accept the power request."); }
    }
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("PowrProf.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
