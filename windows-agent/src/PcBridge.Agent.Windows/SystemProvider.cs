using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcBridge.Agent.Core;

namespace PcBridge.Agent.Windows;

public sealed class SystemProvider(TimeSpan interval) : ISensorProvider
{
    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total");
    private bool _primed;
    public string Name => "System";
    public TimeSpan Interval => interval;

    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("uptime", "sensor", "Uptime", "duration", "s"),
        new("cpu_usage", "sensor", "CPU usage", null, "%"),
        new("memory_usage", "sensor", "Memory usage", null, "%"),
        new("memory_available", "sensor", "Memory available", "data_size", "MB"),
        new("idle_time", "sensor", "Idle time", "duration", "s"),
        new("last_input_time", "sensor", "Last input time", "timestamp"),
        new("locked", "binary_sensor", "Locked", "lock"),
        new("awake", "binary_sensor", "Awake", "running"),
        new("power_state", "sensor", "Power state"),
        new("boot_time", "sensor", "Boot time", "timestamp")
    ];

    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_primed) { _cpu.NextValue(); _primed = true; }
        var cpu = Math.Clamp(_cpu.NextValue(), 0, 100);
        var memory = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(memory)) throw new InvalidOperationException("Windows did not provide memory status.");
        var idleSeconds = GetIdleTime().TotalSeconds;
        var boot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        IReadOnlyList<EntityState> states =
        [
            new("uptime", Math.Round(Environment.TickCount64 / 1000d)),
            new("cpu_usage", Math.Round(cpu, 1)),
            new("memory_usage", Math.Round((double)memory.MemoryLoad, 1)),
            new("memory_available", Math.Round(memory.AvailablePhysical / 1024d / 1024d)),
            new("idle_time", Math.Round(idleSeconds)),
            new("last_input_time", DateTimeOffset.Now.Subtract(TimeSpan.FromSeconds(idleSeconds)).ToUniversalTime().ToString("O")),
            new("locked", IsWorkstationLocked()),
            new("awake", true),
            new("power_state", idleSeconds >= 300 ? "Idle" : "Awake"),
            new("boot_time", boot.ToUniversalTime().ToString("O"))
        ];
        return Task.FromResult(states);
    }

    public ValueTask DisposeAsync() { _cpu.Dispose(); return ValueTask.CompletedTask; }

    private static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : TimeSpan.Zero;
    }

    private static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>(); public uint MemoryLoad;
        public ulong TotalPhysical; public ulong AvailablePhysical; public ulong TotalPageFile; public ulong AvailablePageFile;
        public ulong TotalVirtual; public ulong AvailableVirtual; public ulong AvailableExtendedVirtual;
    }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}

public sealed class NetworkProvider(TimeSpan interval) : ISensorProvider
{
    private long _previousReceived; private long _previousSent; private DateTimeOffset _previousAt;
    public string Name => "Network"; public TimeSpan Interval => interval;
    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("local_ip", "sensor", "Local IP", null, null, false, "diagnostic"),
        new("network_download", "sensor", "Network download", "data_rate", "kB/s"),
        new("network_upload", "sensor", "Network upload", "data_rate", "kB/s")
    ];
    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(a => a.OperationalStatus == OperationalStatus.Up && a.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
        var received = adapters.Sum(a => a.GetIPv4Statistics().BytesReceived);
        var sent = adapters.Sum(a => a.GetIPv4Statistics().BytesSent);
        var now = DateTimeOffset.UtcNow;
        var seconds = Math.Max((now - _previousAt).TotalSeconds, 0.001);
        var down = _previousAt == default ? 0 : Math.Max(0, received - _previousReceived) / 1024d / seconds;
        var up = _previousAt == default ? 0 : Math.Max(0, sent - _previousSent) / 1024d / seconds;
        _previousReceived = received; _previousSent = sent; _previousAt = now;
        var ip = adapters.SelectMany(a => a.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString();
        return Task.FromResult<IReadOnlyList<EntityState>>([new("local_ip", ip), new("network_download", Math.Round(down, 1)), new("network_upload", Math.Round(up, 1))]);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class StorageProvider(TimeSpan interval) : ISensorProvider
{
    public string Name => "Storage";
    public TimeSpan Interval => interval;
    public IReadOnlyList<EntityDescriptor> Describe() =>
    [
        new("disk_free_percent", "sensor", "Disk free", null, "%"),
        new("disk_free", "sensor", "Disk free space", "data_size", "GB"),
        new("disk_total", "sensor", "Disk total space", "data_size", "GB", false, "diagnostic")
    ];

    public Task<IReadOnlyList<EntityState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = new DriveInfo(system);
        if (!drive.IsReady) return Task.FromResult<IReadOnlyList<EntityState>>([]);
        var total = drive.TotalSize / 1024d / 1024d / 1024d;
        var free = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var percent = total <= 0 ? 0 : Math.Round(free / total * 100, 1);
        return Task.FromResult<IReadOnlyList<EntityState>>([
            new("disk_free_percent", percent),
            new("disk_free", Math.Round(free, 1)),
            new("disk_total", Math.Round(total, 1))
        ]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class DeviceInformation
{
    public static (string Manufacturer, string Model) Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            var item = results.Cast<ManagementObject>().FirstOrDefault();
            return (item?["Manufacturer"]?.ToString() ?? "Unknown", item?["Model"]?.ToString() ?? "Windows PC");
        }
        catch { return ("Unknown", "Windows PC"); }
    }
}
