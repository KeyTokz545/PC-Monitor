// Activation / désactivation du pavé tactile, via l'API de gestion des périphériques de Windows
// (on active ou désactive le périphérique, comme dans le Gestionnaire de périphériques). Effet immédiat.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCMonitor;

public static class Touchpad
{
    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    const uint SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_FRIENDLYNAME = 0xC;
    const uint DIF_PROPERTYCHANGE = 0x12;
    const uint DICS_ENABLE = 0x1, DICS_DISABLE = 0x2, DICS_FLAG_GLOBAL = 0x1;
    const uint CM_PROB_DISABLED = 0x16;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDevinfoData { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SpClassinstallHeader { public int cbSize; public uint InstallFunction; }

    [StructLayout(LayoutKind.Sequential)]
    struct SpPropchangeParams { public SpClassinstallHeader ClassInstallHeader; public uint StateChange; public uint Scope; public uint HwProfile; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData info);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData info, uint prop, out uint regType, byte[] buf, uint size, out uint reqSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SpDevinfoData info, ref SpPropchangeParams p, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint func, IntPtr set, ref SpDevinfoData info);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);

    static string Prop(IntPtr set, ref SpDevinfoData info, uint prop)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, prop, out _, buf, (uint)buf.Length, out var req)) return "";
        var s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req, buf.Length));
        return s.TrimEnd('\0').Replace('\0', ' ');
    }

    static bool IsTouchpad(string desc, string hw)
    {
        string t = (desc + " " + hw).ToLowerInvariant().Replace(" ", "");
        // on cible bien le pavé tactile, pas l'écran tactile
        if (t.Contains("touchscreen")) return false;
        return t.Contains("touchpad") || t.Contains("trackpad") || t.Contains("precisiontouchpad") || t.Contains("asueptp");
    }

    /// <summary>Renvoie (trouvé, activé) pour le pavé tactile, sans rien changer.</summary>
    public static (bool found, bool enabled) State()
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == (IntPtr)(-1)) return (false, false);
        try
        {
            var info = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var desc = Prop(set, ref info, SPDRP_DEVICEDESC);
                var fn = Prop(set, ref info, SPDRP_FRIENDLYNAME);
                var hw = Prop(set, ref info, SPDRP_HARDWAREID);
                if (!IsTouchpad(desc + " " + fn, hw)) continue;
                bool enabled = true;
                if (CM_Get_DevNode_Status(out _, out var problem, info.DevInst, 0) == 0)
                    enabled = problem != CM_PROB_DISABLED;
                return (true, enabled);
            }
            return (false, false);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Active ou désactive le pavé tactile. Effet immédiat.</summary>
    public static (bool ok, string msg) Set(bool on)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == (IntPtr)(-1)) return (false, "Accès aux périphériques impossible");
        try
        {
            var info = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var desc = Prop(set, ref info, SPDRP_DEVICEDESC);
                var fn = Prop(set, ref info, SPDRP_FRIENDLYNAME);
                var hw = Prop(set, ref info, SPDRP_HARDWAREID);
                if (!IsTouchpad(desc + " " + fn, hw)) continue;
                var pp = new SpPropchangeParams
                {
                    ClassInstallHeader = new SpClassinstallHeader
                    {
                        cbSize = Marshal.SizeOf<SpClassinstallHeader>(),
                        InstallFunction = DIF_PROPERTYCHANGE,
                    },
                    StateChange = on ? DICS_ENABLE : DICS_DISABLE,
                    Scope = DICS_FLAG_GLOBAL,
                    HwProfile = 0,
                };
                if (!SetupDiSetClassInstallParams(set, ref info, ref pp, Marshal.SizeOf<SpPropchangeParams>())
                    || !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref info))
                    return (false, "Windows a refusé (lance PC Monitor en administrateur)");
                return (true, on ? "Pavé tactile activé" : "Pavé tactile désactivé");
            }
            return (false, "Pavé tactile introuvable sur ce PC");
        }
        catch (Exception e)
        {
            Log.Error("Pavé tactile", e);
            return (false, "Erreur : " + e.Message);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
