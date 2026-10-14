using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LUMA.Services;

public record AudioDeviceItem(
    string Id,
    string Name,
    string TypeName, // "Speakers", "Headphones", "Display", "Bluetooth"
    string Glyph,
    bool IsDefault
);

/// <summary>
/// Native Windows Master Volume and Audio Output Device switching using CoreAudio & IPolicyConfig.
/// </summary>
public static class AudioService
{
    private const int ClsCtxAll = 23;
    private const int DeviceStateActive = 0x00000001;
    private const int eRender = 0;
    private const int eConsole = 0;
    private const int eMultimedia = 1;
    private const int eCommunications = 2;

    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);
        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig]
        int GetState(out int pdwState);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(Guid f, uint p)
        {
            fmtid = f;
            pid = p;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey PkeyDeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int eRole);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(ref Guid pguidEventContext);
        int VolumeStepDown(ref Guid pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    public static List<AudioDeviceItem> GetAudioOutputDevices()
    {
        var list = new List<AudioDeviceItem>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var defaultDev) == 0 && defaultDev != null)
            {
                defaultDev.GetId(out defaultId);
            }

            if (enumerator.EnumAudioEndpoints(eRender, DeviceStateActive, out var collection) == 0 && collection != null)
            {
                if (collection.GetCount(out var count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var dev) == 0 && dev != null)
                        {
                            dev.GetId(out var devId);
                            var name = GetDeviceName(dev);
                            if (string.IsNullOrWhiteSpace(name)) name = "Audio Output";

                            var (typeName, glyph) = CategorizeAudioDevice(name);
                            bool isDef = !string.IsNullOrEmpty(defaultId) && string.Equals(devId, defaultId, StringComparison.OrdinalIgnoreCase);

                            list.Add(new AudioDeviceItem(devId, name, typeName, glyph, isDef));
                        }
                    }
                }
            }
        }
        catch { }

        return list;
    }

    public static string GetCurrentOutputDeviceName()
    {
        try
        {
            var devices = GetAudioOutputDevices();
            var active = devices.Find(d => d.IsDefault);
            if (active != null)
            {
                return CleanDeviceName(active.Name);
            }
        }
        catch { }

        return "Speakers";
    }

    public static bool SetDefaultAudioEndpoint(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            policy.SetDefaultEndpoint(deviceId, eConsole);
            policy.SetDefaultEndpoint(deviceId, eMultimedia);
            policy.SetDefaultEndpoint(deviceId, eCommunications);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDeviceName(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
            {
                var key = PkeyDeviceFriendlyName;
                if (store.GetValue(ref key, out var pv) == 0 && pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                {
                    var res = Marshal.PtrToStringUni(pv.pwszVal);
                    PropVariantClear(ref pv);
                    if (!string.IsNullOrWhiteSpace(res)) return res;
                }

                key = PkeyDeviceDesc;
                if (store.GetValue(ref key, out pv) == 0 && pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                {
                    var res = Marshal.PtrToStringUni(pv.pwszVal);
                    PropVariantClear(ref pv);
                    if (!string.IsNullOrWhiteSpace(res)) return res;
                }
            }
        }
        catch { }

        return "";
    }

    private static (string typeName, string glyph) CategorizeAudioDevice(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("headphone") || lower.Contains("headset") || lower.Contains("наушник") ||
            lower.Contains("airpod") || lower.Contains("earbuds") || lower.Contains("buds") || lower.Contains("freebuds"))
        {
            return ("Headphones", "\uE7F6");
        }
        if (lower.Contains("hdmi") || lower.Contains("display") || lower.Contains("tv") || lower.Contains("монитор") ||
            lower.Contains("amd") || lower.Contains("nvidia") || lower.Contains("intel") || lower.Contains("dell") || lower.Contains("samsung"))
        {
            return ("Display", "\uE7F4");
        }
        if (lower.Contains("bluetooth") || lower.Contains("bt"))
        {
            return ("Bluetooth", "\uE702");
        }
        return ("Speakers", "\uE767");
    }

    public static string CleanDeviceName(string raw)
    {
        if (raw.Contains("AirPods", StringComparison.OrdinalIgnoreCase)) return "AirPods Pro";
        if (raw.Contains("Headphones", StringComparison.OrdinalIgnoreCase) || raw.Contains("Наушники", StringComparison.OrdinalIgnoreCase)) return "Headphones";
        if (raw.Contains("Динамики", StringComparison.OrdinalIgnoreCase)) return "Speakers";
        if (raw.Contains("Speakers", StringComparison.OrdinalIgnoreCase)) return "Speakers";
        if (raw.Length > 20) return raw[..18] + "...";
        return raw;
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) == 0 && device != null)
            {
                var iid = IidIAudioEndpointVolume;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var endpointObj) == 0 && endpointObj is IAudioEndpointVolume endpointVolume)
                {
                    return endpointVolume;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Reads current Windows master volume (0.0 to 1.0).
    /// </summary>
    public static float GetMasterVolume()
    {
        try
        {
            var endpoint = GetEndpointVolume();
            if (endpoint != null && endpoint.GetMasterVolumeLevelScalar(out var level) == 0)
            {
                return Math.Clamp(level, 0.0f, 1.0f);
            }
        }
        catch { }

        return 0.28f;
    }

    /// <summary>
    /// Sets Windows master volume (0.0 to 1.0).
    /// </summary>
    public static void SetMasterVolume(float level)
    {
        try
        {
            var endpoint = GetEndpointVolume();
            if (endpoint != null)
            {
                var ctx = Guid.Empty;
                var clamped = Math.Clamp(level, 0.0f, 1.0f);
                endpoint.SetMasterVolumeLevelScalar(clamped, ref ctx);
            }
        }
        catch { }
    }
}
