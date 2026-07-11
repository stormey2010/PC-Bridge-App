using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

/// <summary>
/// Launches processes on the interactive desktop. Required because the agent runs as a Windows service (session 0).
/// </summary>
public static class InteractiveProcess
{
    public static CommandResult Launch(string name, string executablePath, string arguments, bool requiresElevation, bool hidden = false) =>
        LaunchCore(name, executablePath, arguments, requiresElevation, wait: false, outFile: null, timeout: null, hidden);

    /// <summary>Run a process in the active user session and wait for exit (optionally writing JSON to outFile).</summary>
    public static CommandResult Run(string name, string executablePath, string arguments, TimeSpan timeout, string? outFile = null, bool hidden = true) =>
        LaunchCore(name, executablePath, arguments, requiresElevation: false, wait: true, outFile, timeout, hidden);

    public static bool IsServiceSession()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || !Environment.UserInteractive;
        }
        catch { return true; }
    }

    public static string? FindSessionHelper()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PcBridge.SessionHelper.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "PcBridge.SessionHelper.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PC Bridge Agent", "service", "PcBridge.SessionHelper.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PC Bridge Agent", "PcBridge.SessionHelper.exe")
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static CommandResult LaunchCore(string name, string executablePath, string arguments, bool requiresElevation, bool wait, string? outFile, TimeSpan? timeout, bool hidden)
    {
        try
        {
            var fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
            if (!File.Exists(fullPath)) return new(false, "missing_file", "The configured executable was not found on this PC.");

            if (!IsServiceSession())
            {
                var start = new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.SystemDirectory,
                    UseShellExecute = !wait && !hidden,
                    WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    CreateNoWindow = hidden || wait
                };
                if (requiresElevation) start.Verb = "runas";
                if (wait)
                {
                    start.UseShellExecute = false;
                    start.RedirectStandardOutput = true;
                    start.RedirectStandardError = true;
                }
                using var process = Process.Start(start);
                if (process is null) return new(false, "start_failed", $"Windows could not start {name}.");
                if (!wait) return Success(name, requiresElevation);
                if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds))
                {
                    try { process.Kill(true); } catch { /* ignore */ }
                    return new(false, "timeout", $"{name} timed out.");
                }
                return process.ExitCode == 0 ? Success(name, requiresElevation) : new(false, "windows_error", $"{name} failed with code {process.ExitCode}.");
            }

            if (requiresElevation)
            {
                var escapedPath = fullPath.Replace("'", "''");
                var escapedArgs = (arguments ?? string.Empty).Replace("'", "''");
                var ps = string.IsNullOrWhiteSpace(arguments)
                    ? $"Start-Process -FilePath '{escapedPath}' -Verb RunAs -WindowStyle Hidden"
                    : $"Start-Process -FilePath '{escapedPath}' -ArgumentList '{escapedArgs}' -Verb RunAs -WindowStyle Hidden";
                return LaunchInActiveSession(name, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"", elevated: true, wait: false, outFile: null, timeout: null, hidden: true);
            }

            return LaunchInActiveSession(name, fullPath, arguments ?? string.Empty, elevated: false, wait, outFile, timeout, hidden);
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
        new(true, null, elevated ? $"{name} launched (administrator approval may be required)." : $"{name} completed.");

    private static CommandResult LaunchInActiveSession(string name, string fullPath, string arguments, bool elevated, bool wait, string? outFile, TimeSpan? timeout, bool hidden)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return new(false, "no_session", "No interactive Windows session is available. Sign in on the PC first.");
        if (!WTSQueryUserToken(sessionId, out var userToken))
            return new(false, "no_session", "Could not access the signed-in Windows session.");

        IntPtr env = IntPtr.Zero;
        IntPtr primary = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, 0x10000000, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return new(false, "windows_error", "Could not prepare the interactive session token.");
            CreateEnvironmentBlock(out env, primary, false);

            var args = arguments ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(outFile))
                args = string.IsNullOrWhiteSpace(args) ? $"--out \"{outFile}\"" : $"{args} --out \"{outFile}\"";

            var commandLine = new StringBuilder(string.IsNullOrWhiteSpace(args) ? $"\"{fullPath}\"" : $"\"{fullPath}\" {args}");
            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                LpDesktop = "winsta0\\default",
                DwFlags = hidden ? StartfUseShowWindow : 0,
                WShowWindow = hidden ? SwHide : SwShow
            };
            const uint createUnicodeEnvironment = 0x00000400;
            const uint createNoWindow = 0x08000000;
            const uint createNewConsole = 0x00000010;
            // Hidden helpers must not allocate a console (that was flashing a terminal every audio poll).
            var flags = createUnicodeEnvironment | (hidden ? createNoWindow : createNewConsole);
            if (!CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, env,
                    Path.GetDirectoryName(fullPath), ref startup, out var processInfo))
                return new(false, "start_failed", $"Windows could not start {name} in the user session.");

            try
            {
                if (!wait)
                {
                    CloseHandle(processInfo.HProcess);
                    CloseHandle(processInfo.HThread);
                    return Success(name, elevated);
                }

                var ms = (uint)Math.Max(1000, (timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds);
                var waitResult = WaitForSingleObject(processInfo.HProcess, ms);
                if (waitResult != 0)
                {
                    TerminateProcess(processInfo.HProcess, 1);
                    return new(false, "timeout", $"{name} timed out in the user session.");
                }
                GetExitCodeProcess(processInfo.HProcess, out var exitCode);
                return exitCode == 0
                    ? Success(name, elevated)
                    : new(false, "windows_error", $"{name} failed with code {exitCode}.");
            }
            finally
            {
                CloseHandle(processInfo.HProcess);
                CloseHandle(processInfo.HThread);
            }
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
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const short SwShow = 5;

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
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
