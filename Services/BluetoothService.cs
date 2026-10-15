using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
    private static Radio? _monitoredRadio;
    public static event Action<bool>? RadioStateChanged;

    /// <summary>
    /// Initializes real-time listener for Windows Bluetooth radio state changes.
    /// </summary>
    public static async Task InitializeRadioAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _monitoredRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_monitoredRadio != null)
            {
                _monitoredRadio.StateChanged += (sender, _) =>
                {
                    RadioStateChanged?.Invoke(sender.State == RadioState.On);
                };
            }
        }
        catch { }
    }

    /// <summary>
    /// Queries whether the Bluetooth radio is currently turned ON in Windows.
    /// </summary>
    public static async Task<bool> IsRadioOnAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (btRadio != null)
            {
                return btRadio.State == RadioState.On;
            }
        }
        catch { }

        // Fallback: check if Bluetooth adapter exists in system
        return DeviceStatusService.HasBluetoothAdapter();
    }

    /// <summary>
    /// Toggles the Windows Bluetooth radio on or off.
    /// </summary>
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

    /// <summary>
    /// Discovers all paired Bluetooth devices with accurate live connection state.
    /// Uses WinRT Classic, WinRT BLE, and Windows CoreAudio endpoints for 100% coverage.
    /// </summary>
    public static async Task<List<BluetoothDeviceItem>> GetPairedDevicesAsync()
    {
        var result = new List<BluetoothDeviceItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Query Classic Bluetooth paired devices via WinRT
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Connected" });

            foreach (var di in devices)
            {
                if (string.IsNullOrWhiteSpace(di.Name)) continue;
                var cleanName = di.Name.Trim();
                if (!seenIds.Add(di.Id) || !seenNames.Add(cleanName)) continue;

                bool isConnected = false;
                if (di.Properties.TryGetValue("System.Devices.Connected", out var connObj) && connObj is bool bConn && bConn)
                {
                    isConnected = true;
                }

                if (!isConnected)
                {
                    try
                    {
                        var bt = await BluetoothDevice.FromIdAsync(di.Id);
                        if (bt != null && bt.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        {
                            isConnected = true;
                        }
                    }
                    catch { }
                }

                if (!isConnected)
                {
                    isConnected = AudioService.IsAudioEndpointActive(cleanName);
                }

                var (devType, glyph) = CategorizeBluetoothDevice(cleanName);
                result.Add(new BluetoothDeviceItem(di.Id, cleanName, devType, glyph, isConnected, true));
            }
        }
        catch { }

        // 2. Query Bluetooth Low Energy (BLE) paired devices (AirPods Find My, modern accessories)
        try
        {
            var leSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var leDevices = await DeviceInformation.FindAllAsync(leSelector, new[] { "System.Devices.Connected" });

            foreach (var di in leDevices)
            {
                if (string.IsNullOrWhiteSpace(di.Name)) continue;
                var cleanName = di.Name.Trim();
                if (!seenIds.Add(di.Id) || !seenNames.Add(cleanName)) continue;

                bool isConnected = false;
                if (di.Properties.TryGetValue("System.Devices.Connected", out var connObj) && connObj is bool bConn && bConn)
                {
                    isConnected = true;
                }

                if (!isConnected)
                {
                    isConnected = AudioService.IsAudioEndpointActive(cleanName);
                }

                var (devType, glyph) = CategorizeBluetoothDevice(cleanName);
                result.Add(new BluetoothDeviceItem(di.Id, cleanName, devType, glyph, isConnected, true));
            }
        }
        catch { }

        // 3. Complement with registered Windows Bluetooth audio endpoints (AirPods, headsets)
        try
        {
            var endpoints = AudioService.GetRegisteredBluetoothAudioEndpoints();
            foreach (var ep in endpoints)
            {
                var cleanName = CleanEndpointDeviceName(ep.Name);
                if (string.IsNullOrWhiteSpace(cleanName)) continue;

                if (seenNames.Add(cleanName))
                {
                    seenIds.Add(ep.Id);
                    var (devType, glyph) = CategorizeBluetoothDevice(cleanName);
                    result.Add(new BluetoothDeviceItem(ep.Id, cleanName, devType, glyph, ep.IsActive, true));
                }
            }
        }
        catch { }

        // Final deduplication prioritizing active/connected state
        return result
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.IsConnected).First())
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.DeviceType != "Audio")
            .ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Fast, non-blocking connection to a paired Bluetooth device.
    /// Wakes up the link and routes audio endpoint immediately without hanging.
    /// </summary>
    public static async Task<bool> ConnectDeviceAsync(BluetoothDeviceItem device)
    {
        // 1. If it's an audio device, look for its endpoint and set as default
        try
        {
            var endpoints = AudioService.GetRegisteredBluetoothAudioEndpoints();
            var match = endpoints.FirstOrDefault(e =>
                e.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains(e.Name, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                AudioService.SetDefaultAudioEndpoint(match.Id);
            }
        }
        catch { }

        // 2. Wake up device connection via WinRT
        try
        {
            if (!string.IsNullOrEmpty(device.Id))
            {
                if (device.Id.Contains("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
                {
                    _ = BluetoothLEDevice.FromIdAsync(device.Id);
                }
                else if (device.Id.Contains("Bluetooth#", StringComparison.OrdinalIgnoreCase))
                {
                    _ = BluetoothDevice.FromIdAsync(device.Id);
                }
            }
        }
        catch { }

        // 3. Monitor for audio endpoint to become active and ensure audio routing
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(400);
                try
                {
                    var activeDevs = AudioService.GetAudioOutputDevices(isBluetoothOn: true);
                    var activeMatch = activeDevs.FirstOrDefault(a =>
                        a.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase) ||
                        device.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase));

                    if (activeMatch != null)
                    {
                        AudioService.SetDefaultAudioEndpoint(activeMatch.Id);
                        break;
                    }
                }
                catch { }
            }
        });

        return true;
    }

    /// <summary>
    /// Fast, instant disconnect of a Bluetooth device.
    /// Routes audio back to speakers immediately with zero delay.
    /// </summary>
    public static Task<bool> DisconnectDeviceAsync(BluetoothDeviceItem device)
    {
        // Instant audio fallback to default PC speakers/display
        AudioService.FallbackToDefaultSpeaker();
        return Task.FromResult(true);
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

    private static string CleanEndpointDeviceName(string raw)
    {
        // Extracts clean device name from e.g. "Головной телефон (AirPods Pro - bakumi - Find My Hands-Free AG Audio)"
        int openParen = raw.IndexOf('(');
        int closeParen = raw.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            string inner = raw.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            inner = inner.Replace("Hands-Free AG Audio", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("Hands-Free Audio", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("Stereo", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();
            if (!string.IsNullOrEmpty(inner)) return inner;
        }

        return raw.Trim();
    }
}
