using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32;

namespace LUMA.Services;

internal sealed class HardwareMonitor
{
    private ulong? _lastIdle;
    private ulong? _lastKernel;
    private ulong? _lastUser;
    private long? _lastReceived;
    private long? _lastSent;
    private DateTime _lastNetworkRead = DateTime.UtcNow;
    private readonly string _cpuModel = ReadCpuModel();

    public void ResetNetworkBaseline()
    {
        _lastReceived = null;
        _lastSent = null;
        _lastNetworkRead = DateTime.UtcNow;
    }

    public SystemSnapshot Read()
    {
        var cpuPercent = ReadCpuPercent();
        var memory = ReadMemory();
        var disk = ReadSystemDisk();
        var network = ReadNetwork();

        return new SystemSnapshot(
            cpuPercent,
            Environment.ProcessorCount,
            _cpuModel,
            memory.Percent,
            memory.UsedGb,
            memory.TotalGb,
            disk.Percent,
            disk.UsedGb,
            disk.TotalGb,
            network.DownloadBytesPerSecond,
            network.UploadBytesPerSecond,
            network.Adapters);
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var currentIdle = idle.ToUInt64();
        var currentKernel = kernel.ToUInt64();
        var currentUser = user.ToUInt64();
        var result = 0d;

        if (_lastIdle is { } lastIdle && _lastKernel is { } lastKernel && _lastUser is { } lastUser)
        {
            var idleDelta = currentIdle - lastIdle;
            var totalDelta = (currentKernel - lastKernel) + (currentUser - lastUser);
            if (totalDelta > 0)
            {
                result = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
        }

        _lastIdle = currentIdle;
        _lastKernel = currentKernel;
        _lastUser = currentUser;
        return result;
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            return (0, 0, 0);
        }

        var used = status.TotalPhysical - status.AvailablePhysical;
        var totalGb = status.TotalPhysical / 1024d / 1024d / 1024d;
        var usedGb = used / 1024d / 1024d / 1024d;
        return (100d * used / status.TotalPhysical, usedGb, totalGb);
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            var drive = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && string.Equals(x.Name, root, StringComparison.OrdinalIgnoreCase));
            if (drive is null || drive.TotalSize <= 0)
            {
                return (0, 0, 0);
            }

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return (100d * used / drive.TotalSize, used / 1024d / 1024d / 1024d, drive.TotalSize / 1024d / 1024d / 1024d);
        }
        catch (IOException)
        {
            return (0, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0, 0);
        }
    }

    private (long DownloadBytesPerSecond, long UploadBytesPerSecond, int Adapters) ReadNetwork()
    {
        long received = 0;
        long sent = 0;
        var count = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                count++;
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while Windows refreshes its network table.
            }
        }

        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.2, (now - _lastNetworkRead).TotalSeconds);
        var down = _lastReceived is { } previousReceived ? Math.Max(0, (long)((received - previousReceived) / elapsed)) : 0;
        var up = _lastSent is { } previousSent ? Math.Max(0, (long)((sent - previousSent) / elapsed)) : 0;
        _lastReceived = received;
        _lastSent = sent;
        _lastNetworkRead = now;

        return (down, up, count);
    }

    private static string ReadCpuModel()
    {
        try
        {
            using var processor = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = processor?.GetValue("ProcessorNameString")?.ToString();
            return string.IsNullOrWhiteSpace(name) ? "Модель процессора недоступна" : name.Trim();
        }
        catch (UnauthorizedAccessException)
        {
            return "Модель процессора недоступна";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        public readonly uint Low;
        public readonly uint High;
        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

internal sealed record SystemSnapshot(
    double CpuPercent,
    int LogicalProcessors,
    string CpuModel,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double DiskPercent,
    double DiskUsedGb,
    double DiskTotalGb,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    int NetworkAdapters);
