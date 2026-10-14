using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace LUMA.Services;

public record BluetoothDeviceItem(
    string Id,
    string Name,
    string DeviceType, // "Audio", "Input", "Other"
    string Glyph,
    bool IsConnected,
    bool IsPaired
);

public static class BluetoothService
{
    private static readonly Guid AudioSinkServiceClassUuid = new("0000110b-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HandsfreeServiceClassUuid = new("0000111e-0000-1000-8000-00805f9b34fb");

    public static async Task<List<BluetoothDeviceItem>> GetPairedDevicesAsync()
    {
        var result = new List<BluetoothDeviceItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Query via Windows.Devices.Enumeration (WinRT)
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector);

            foreach (var di in devices)
            {
                if (string.IsNullOrWhiteSpace(di.Name)) continue;
                var cleanName = di.Name.Trim();
                if (!seenIds.Add(di.Id) || !seenNames.Add(cleanName)) continue;

                bool isConnected = false;
                try
                {
                    var bt = await BluetoothDevice.FromIdAsync(di.Id);
                    if (bt != null)
                    {
                        isConnected = bt.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    }
                }
                catch { }

                var (devType, glyph) = CategorizeBluetoothDevice(cleanName);
                result.Add(new BluetoothDeviceItem(di.Id, cleanName, devType, glyph, isConnected, true));
            }
        }
        catch { }

        // 2. Fallback / supplementary check via Registry (BTHENUM) to guarantee discovering AirPods Pro, etc.
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
            if (enumKey != null)
            {
                foreach (var subKeyName in enumKey.GetSubKeyNames())
                {
                    if (!subKeyName.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)) continue;

                    using var devKey = enumKey.OpenSubKey(subKeyName);
                    if (devKey != null)
                    {
                        foreach (var instName in devKey.GetSubKeyNames())
                        {
                            using var instKey = devKey.OpenSubKey(instName);
                            var friendlyName = instKey?.GetValue("FriendlyName") as string
                                               ?? instKey?.GetValue("DeviceDesc") as string;

                            if (!string.IsNullOrWhiteSpace(friendlyName))
                            {
                                var cleanName = friendlyName.Trim();
                                if (!cleanName.Contains("Enumerator", StringComparison.OrdinalIgnoreCase) &&
                                    !cleanName.Contains("Protocol", StringComparison.OrdinalIgnoreCase) &&
                                    !cleanName.Contains("Транспорт", StringComparison.OrdinalIgnoreCase) &&
                                    !cleanName.Contains("Служба", StringComparison.OrdinalIgnoreCase) &&
                                    seenIds.Add(instName) &&
                                    seenNames.Add(cleanName))
                                {
                                    var (devType, glyph) = CategorizeBluetoothDevice(cleanName);
                                    bool connected = IsAudioEndpointConnected(cleanName);
                                    result.Add(new BluetoothDeviceItem(instName, cleanName, devType, glyph, connected, true));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Final deduplication by device name (case-insensitive) prioritizing connected status
        return result
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.IsConnected).First())
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.DeviceType != "Audio")
            .ThenBy(d => d.Name)
            .ToList();
    }

    public static async Task<bool> ToggleRadioAsync(bool enable)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (btRadio != null)
            {
                var targetState = enable ? RadioState.On : RadioState.Off;
                var res = await btRadio.SetStateAsync(targetState);
                return res == RadioAccessStatus.Allowed;
            }
        }
        catch { }

        return false;
    }

    public static async Task<bool> ConnectDeviceAsync(BluetoothDeviceItem device)
    {
        try
        {
            if (!string.IsNullOrEmpty(device.Id))
            {
                var bt = await BluetoothDevice.FromIdAsync(device.Id);
                if (bt != null)
                {
                    var rfcomm = await bt.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                    if (bt.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        // If direct connect is not supported or requires pairing PIN, open Windows Bluetooth flyout
        OpenBluetoothSettings();
        return false;
    }

    public static void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch { }
    }

    private static (string devType, string glyph) CategorizeBluetoothDevice(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("airpod") || lower.Contains("headphone") || lower.Contains("headset") ||
            lower.Contains("наушник") || lower.Contains("earbuds") || lower.Contains("buds") ||
            lower.Contains("freebuds") || lower.Contains("wh-") || lower.Contains("soundcore"))
        {
            return ("Audio", "\uE7F6"); // Headphones icon
        }

        if (lower.Contains("keyboard") || lower.Contains("клавиатур"))
        {
            return ("Input", "\uE765"); // Keyboard icon
        }

        if (lower.Contains("mouse") || lower.Contains("мышь"))
        {
            return ("Input", "\uE962"); // Mouse icon
        }

        if (lower.Contains("phone") || lower.Contains("телефон") || lower.Contains("iphone") || lower.Contains("galaxy"))
        {
            return ("Phone", "\uE8EA"); // Phone icon
        }

        return ("Other", "\uE702"); // Generic Bluetooth icon
    }

    private static bool IsAudioEndpointConnected(string deviceName)
    {
        try
        {
            var audioDevs = AudioService.GetAudioOutputDevices();
            return audioDevs.Any(a => a.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase) ||
                                     deviceName.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
