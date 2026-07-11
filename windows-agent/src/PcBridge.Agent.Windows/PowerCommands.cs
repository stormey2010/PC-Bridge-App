using System.Diagnostics;
using System.Runtime.InteropServices;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class PowerCommandHandler : ICommandHandler
{
    public IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system.lock", "system.sleep", "system.hibernate", "system.logoff", "system.restart", "system.shutdown",
        "system.display_off", "system.abort_shutdown", "system.open_explorer", "system.open_settings"
    };

    public async Task<CommandResult> ExecuteAsync(string command, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? parameters, CancellationToken cancellationToken)
    {
        return command switch
        {
            "system.lock" => LockSession(),
            "system.sleep" => SetSuspendState(false, true, true)
                ? new(true, null, "Sleep request accepted.")
                : new(false, "windows_error", "Windows rejected the sleep request."),
            "system.hibernate" => SetSuspendState(true, true, true)
                ? new(true, null, "Hibernate request accepted.")
                : new(false, "windows_error", "Windows rejected the hibernate request."),
            "system.logoff" => LogOffSession(),
            "system.display_off" => DisplayOff(),
            "system.abort_shutdown" => await RunShutdownAsync("/a", "Shutdown cancelled.", cancellationToken),
            "system.open_explorer" => InteractiveProcess.Launch("File Explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), string.Empty, false),
            "system.open_settings" => InteractiveProcess.Launch("Settings", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c start ms-settings:", false),
            "system.restart" => await RunShutdownAsync("/r /t 0", "Restart accepted by Windows.", cancellationToken),
            "system.shutdown" => await RunShutdownAsync("/s /t 0", "Shutdown accepted by Windows.", cancellationToken),
            _ => new(false, "unknown_command", "Unsupported power command.")
        };
    }

    private static CommandResult LockSession()
    {
        // LockWorkStation fails in session 0 — run it on the interactive desktop.
        var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        return InteractiveProcess.Launch("Lock", rundll, "user32.dll,LockWorkStation", requiresElevation: false);
    }

    private static CommandResult LogOffSession()
    {
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        return InteractiveProcess.Launch("Log off", shutdown, "/l", requiresElevation: false);
    }

    private static CommandResult DisplayOff()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        const string script = "(Add-Type '[DllImport(\"user32.dll\")] public static extern int SendMessage(int h,int m,int w,int l);' -Name W -Pas)::SendMessage(-1,0x0112,0xF170,2)";
        return InteractiveProcess.Launch("Display off", powershell, $"-NoProfile -WindowStyle Hidden -Command \"{script}\"", requiresElevation: false);
    }

    private static async Task<CommandResult> RunShutdownAsync(string arguments, string success, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return new(false, "start_failed", "Windows could not start the shutdown request.");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? new(true, null, success) : new(false, "windows_error", $"Windows rejected the request with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, "windows_error", "Windows could not accept the power request."); }
    }

    [DllImport("PowrProf.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
