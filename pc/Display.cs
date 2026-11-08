using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PCMonitor;

/// <summary>Écran : luminosité, fréquence de rafraîchissement, extinction.</summary>
public static class Display
{
    // ------------------------------------------------------------- luminosité
    public static int? GetBrightness()
    {
        try
        {
            using var mclass = new ManagementClass("WmiMonitorBrightness") { Scope = new ManagementScope(@"\\.\root\wmi") };
            using var instances = mclass.GetInstances();
            foreach (ManagementObject instance in instances)
                return Convert.ToInt32(instance.GetPropertyValue("CurrentBrightness"));
        }
        catch (Exception e)
        {
            Log.Error("Lecture de la luminosité", e);
        }
        return null;
    }

    public static bool SetBrightness(int value)
    {
        try
        {
            value = Math.Clamp(value, 0, 100);
            using var mclass = new ManagementClass("WmiMonitorBrightnessMethods") { Scope = new ManagementScope(@"\\.\root\wmi") };
            using var instances = mclass.GetInstances();
            foreach (ManagementObject instance in instances)
            {
                instance.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Error("Réglage de la luminosité", e);
        }
        return false;
    }

    // ------------------------------------------------------------- fréquence
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_UPDATEREGISTRY = 0x1;

    static DEVMODE NewMode() => new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    static string Device => Screen.PrimaryScreen?.DeviceName;

    /// <summary>Fréquence actuelle et fréquences possibles à la résolution actuelle.</summary>
    public static (int Current, List<int> Available) Refresh()
    {
        var available = new List<int>();
        int current = 0;
        try
        {
            var cur = NewMode();
            if (!EnumDisplaySettings(Device, ENUM_CURRENT_SETTINGS, ref cur)) return (0, available);
            current = cur.dmDisplayFrequency;
            var m = NewMode();
            for (int i = 0; EnumDisplaySettings(Device, i, ref m); i++)
            {
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel
                    && m.dmDisplayFrequency > 1 && !available.Contains(m.dmDisplayFrequency))
                    available.Add(m.dmDisplayFrequency);
                m = NewMode();
            }
        }
        catch (Exception e)
        {
            Log.Error("Fréquences de l'écran", e);
        }
        available.Sort();
        return (current, available);
    }

    public static bool SetRefresh(int hz)
    {
        try
        {
            var mode = NewMode();
            if (!EnumDisplaySettings(Device, ENUM_CURRENT_SETTINGS, ref mode)) return false;
            mode.dmDisplayFrequency = hz;
            mode.dmFields = DM_DISPLAYFREQUENCY;
            var result = ChangeDisplaySettingsEx(Device, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            Log.Info($"Fréquence {hz} Hz : {result}");
            return result == 0;
        }
        catch (Exception e)
        {
            Log.Error("Changement de fréquence", e);
            return false;
        }
    }

    // ------------------------------------------------------------- extinction
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Éteint l'écran (il se rallume au premier mouvement de souris ou touche).</summary>
    public static void Off() => PostMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));
}
