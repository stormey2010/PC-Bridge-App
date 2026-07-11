using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

/// <summary>
/// Launches processes on the interactive desktop. Required because the agent runs as a Windows service (session 0).
/// </summary>
internal static class InteractiveProcess
{
    public static CommandResult Launch(string name, string executablePath, string arguments, bool requiresElevation)
    {
        try
        {
            var fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
            if (!File.Exists(fullPath)) return new(false, "missing_file", "The configured executable was not found on this PC.");

            // When already running interactively (UI / debug), use a normal start.
            if (!IsServiceSession())
            {
                var start = new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.SystemDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                if (requiresElevation) start.Verb = "runas";
                using var process = Process.Start(start);
                if (process is null) return new(false, "start_failed", $"Windows could not start {name}.");
                return Success(name, requiresElevation);
            }

            if (requiresElevation)
            {
                // Start-Process -Verb RunAs inside the user session so UAC appears on their desktop.
                var escapedPath = fullPath.Replace("'", "''");
                var escapedArgs = (arguments ?? string.Empty).Replace("'", "''");
                var ps = string.IsNullOrWhiteSpace(arguments)
                    ? $"Start-Process -FilePath '{escapedPath}' -Verb RunAs"
                    : $"Start-Process -FilePath '{escapedPath}' -ArgumentList '{escapedArgs}' -Verb RunAs";
                return LaunchInActiveSession(name, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"", elevated: true);
            }

            return LaunchInActiveSession(name, fullPath, arguments ?? string.Empty, elevated: false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "cancelled", "Administrator approval was cancelled.");
        }
        catch (Exception)
        {
            return new(false, "windows_error", $"Windows could not launch {name}.");
        }
    }

    private static CommandResult Success(string name, bool elevated) =>
        new(true, null, elevated ? $"{name} launched (administrator approval may be required)." : $"{name} launched.");

    private static bool IsServiceSession()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || !Environment.UserInteractive;
        }
        catch { return true; }
    }

    private static CommandResult LaunchInActiveSession(string name, string fullPath, string arguments, bool elevated)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return new(false, "no_session", "No interactive Windows session is available.");
        if (!WTSQueryUserToken(sessionId, out var userToken))
            return new(false, "no_session", "Could not access the signed-in Windows session.");

        IntPtr env = IntPtr.Zero;
        IntPtr primary = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, 0x10000000, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return new(false, "windows_error", "Could not prepare the interactive session token.");
            CreateEnvironmentBlock(out env, primary, false);

            var commandLine = new System.Text.StringBuilder(string.IsNullOrWhiteSpace(arguments)
                ? $"\"{fullPath}\""
                : $"\"{fullPath}\" {arguments}");
            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                LpDesktop = "winsta0\\default"
            };
            const uint createUnicodeEnvironment = 0x00000400;
            const uint createNewConsole = 0x00000010;
            if (!CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    createUnicodeEnvironment | createNewConsole, env,
                    Path.GetDirectoryName(fullPath), ref startup, out var processInfo))
                return new(false, "start_failed", $"Windows could not start {name} in the user session.");

            CloseHandle(processInfo.HProcess);
            CloseHandle(processInfo.HThread);
            return Success(name, elevated);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? LpReserved;
        public string? LpDesktop;
        public string? LpTitle;
        public int DwX, DwY, DwXSize, DwYSize, DwXCountChars, DwYCountChars, DwFillAttribute, DwFlags;
        public short WShowWindow, CbReserved2;
        public IntPtr LpReserved2, HStdInput, HStdOutput, HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr HProcess, HThread;
        public int DwProcessId, DwThreadId;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
