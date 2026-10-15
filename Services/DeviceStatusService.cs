using System;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace LUMA.Services;

public static class DeviceStatusService
{
    public static bool HasWifiAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                ni.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("802.11", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static bool HasBluetoothAdapter()
    {
        try
        {
            // Check Windows services and radios in Registry
            using var radiosKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Radios");
            if (radiosKey != null && radiosKey.GetSubKeyNames().Length > 0)
            {
                return true;
            }

            // Check if any network interface or device is Bluetooth
            if (NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                ni.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            using var bthKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHUSB");
            if (bthKey != null)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool IsWifiEnabled()
    {
        if (!HasWifiAdapter()) return false;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                 ni.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)) &&
                ni.OperationalStatus == OperationalStatus.Up);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ToggleWifiRadioAsync(bool enable)
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
            if (wifi != null)
            {
                var targetState = enable ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off;
                var res = await wifi.SetStateAsync(targetState);
                return res == Windows.Devices.Radios.RadioAccessStatus.Allowed;
            }
        }
        catch { }

        try
        {
            var action = enable ? "enabled" : "disabled";
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", $"interface set interface name=\"Wi-Fi\" admin={action}")
            {
                CreateNoWindow = true,
                UseShellExecute = true
            });
            return true;
        }
        catch { }

        return false;
    }
}

