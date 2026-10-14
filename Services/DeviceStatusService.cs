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
}
