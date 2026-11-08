// Commandes ASUS (modes de performance et courbes de ventilateurs) par le pilote ATKACPI.
// Les codes et la façon de les envoyer viennent de G-Helper (https://github.com/seerge/g-helper),
// sous licence GNU GPL v3 : ce projet est donc distribué sous la même licence (voir LICENSE).
using System;
using System.Runtime.InteropServices;

namespace PCMonitor;

public sealed class Asus : IDisposable
{
    const string DeviceName = @"\\.\ATKACPI";
    const uint ControlCode = 0x0022240C;
    const uint DSTS = 0x53545344;
    const uint DEVS = 0x53564544;

    public const uint PerformanceMode = 0x00120075;
    public const uint VivoBookMode = 0x00110019;
    public const uint CpuFan = 0x00110013;
    public const uint GpuFan = 0x00110014;
    public const uint DevsCpuFan = 0x00110022;
    public const uint DevsGpuFan = 0x00110023;
    public const uint DevsCpuFanCurve = 0x00110024;
    public const uint DevsGpuFanCurve = 0x00110025;

    public const uint BatteryLimit = 0x00120057;
    public const uint ScreenOverdrive = 0x00050019;
    public const uint ScreenOverdriveSupport = 0x00050020;
    public const uint MicMuteLed = 0x00040017;

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr device, uint code, byte[] inBuffer, uint inSize, byte[] outBuffer, uint outSize, ref uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    readonly object gate = new object();
    IntPtr handle = new IntPtr(-1);

    public bool Connected { get; private set; }

    public Asus()
    {
        try
        {
            handle = CreateFile(DeviceName, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            Connected = handle != new IntPtr(-1) && handle != IntPtr.Zero;
            Log.Info(Connected ? "Pilote ASUS ATKACPI connecté" : "Pilote ASUS ATKACPI introuvable (erreur " + Marshal.GetLastWin32Error() + ")");
        }
        catch (Exception e)
        {
            Log.Error("Connexion ATKACPI", e);
        }
    }

    byte[] Call(uint method, byte[] args)
    {
        var buffer = new byte[8 + args.Length];
        var output = new byte[16];
        BitConverter.GetBytes(method).CopyTo(buffer, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buffer, 4);
        Array.Copy(args, 0, buffer, 8, args.Length);
        if (!Connected) return output;
        lock (gate)
        {
            uint returned = 0;
            DeviceIoControl(handle, ControlCode, buffer, (uint)buffer.Length, output, (uint)output.Length, ref returned, IntPtr.Zero);
        }
        return output;
    }

    public int Get(uint device)
    {
        var args = new byte[8];
        BitConverter.GetBytes(device).CopyTo(args, 0);
        return BitConverter.ToInt32(Call(DSTS, args), 0) - 65536;
    }

    public int Set(uint device, int value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(device).CopyTo(args, 0);
        BitConverter.GetBytes((uint)value).CopyTo(args, 4);
        return BitConverter.ToInt32(Call(DEVS, args), 0);
    }

    public int Set(uint device, byte[] parameters)
    {
        var args = new byte[4 + parameters.Length];
        BitConverter.GetBytes(device).CopyTo(args, 0);
        parameters.CopyTo(args, 4);
        return BitConverter.ToInt32(Call(DEVS, args), 0);
    }

    public bool IsSupported(uint device) => Get(device) >= 0;

    /// <summary>0 = équilibré, 1 = turbo, 2 = silencieux. Renvoie 1 si le BIOS a accepté.</summary>
    public int SetPerformanceMode(int mode)
    {
        if (IsSupported(PerformanceMode)) return Set(PerformanceMode, mode);
        if (IsSupported(VivoBookMode)) return Set(VivoBookMode, mode == 1 ? 2 : mode == 2 ? 1 : mode);
        return Set(PerformanceMode, mode);
    }

    /// <summary>Vitesse du ventilateur en tr/min, ou -1 si inconnue.</summary>
    public int FanRpm(bool gpu)
    {
        int raw = Get(gpu ? GpuFan : CpuFan);
        int fan = raw & 0xFFFF;
        if (fan > 120 || (fan == 0 && raw < 0)) return -1;
        return fan * 100;
    }

    /// <summary>Courbe de 16 octets : 8 températures (°C) puis 8 vitesses (%).</summary>
    public int SetFanCurve(bool gpu, byte[] curve)
    {
        if (curve == null || curve.Length != 16) return -1;
        return Set(gpu ? DevsGpuFanCurve : DevsCpuFanCurve, (byte[])curve.Clone());
    }

    /// <summary>
    /// Courbe d'usine du BIOS pour un mode (0 équilibré, 1 turbo, 2 silencieux).
    /// Renvoie null si le BIOS ne la donne pas.
    /// </summary>
    public byte[] GetFanCurve(bool gpu, int asusMode)
    {
        uint fanMode = asusMode == 1 ? 2u : asusMode == 2 ? 1u : 0u; // ASUS inverse turbo et silencieux ici
        var args = new byte[8];
        BitConverter.GetBytes(gpu ? DevsGpuFanCurve : DevsCpuFanCurve).CopyTo(args, 0);
        BitConverter.GetBytes(fanMode).CopyTo(args, 4);
        var curve = Call(DSTS, args);
        return IsValidCurve(curve) ? curve : null;
    }

    public static bool IsValidCurve(byte[] c)
    {
        if (c == null || c.Length != 16) return false;
        for (int i = 0; i < 8; i++)
        {
            if (c[i] < 20 || c[i] > 120) return false;
            if (i > 0 && c[i] < c[i - 1]) return false;
            if (c[8 + i] > 100) return false;
        }
        return true;
    }

    public bool OverdriveSupported => Get(ScreenOverdriveSupport) == 1;

    /// <summary>Autre méthode pour certains modèles : vitesse mini/maxi seulement.</summary>
    public int SetFanRange(bool gpu, byte[] curve)
    {
        if (curve == null || curve.Length != 16) return -1;
        byte min = (byte)(curve[8] * 255 / 100);
        byte max = (byte)(curve[15] * 255 / 100);
        return Set(gpu ? DevsGpuFan : DevsCpuFan, new[] { min, max });
    }

    public void Dispose()
    {
        if (Connected) CloseHandle(handle);
        Connected = false;
    }
}
