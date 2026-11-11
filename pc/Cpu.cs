// Réglages du processeur Intel sans ThrottleStop : undervolt (tension), limites de puissance PL1/PL2,
// Turbo et préférence d'énergie, par mode. Accès aux registres du processeur (MSR) par le pilote signé
// PawnIO et son module officiel IntelMSR, qui n'autorise que quelques registres d'écriture.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCMonitor;

public sealed class CpuModeTune
{
    /// <summary>Appliquer les limites de puissance de ce mode (sinon on laisse celles du BIOS).</summary>
    public bool Limits { get; set; }
    public int Pl1 { get; set; } = 45;
    public int Pl2 { get; set; } = 90;
    /// <summary>Durée du turbo (s) avant de redescendre à PL1.</summary>
    public int Tau { get; set; } = 28;
    /// <summary>Préférence d'énergie Speed Shift 0 (perf) à 100 (économie), -1 = laisser Windows.</summary>
    public int Epp { get; set; } = -1;

    public CpuModeTune Clamp()
    {
        Pl1 = Math.Clamp(Pl1, 5, 150);
        Pl2 = Math.Clamp(Pl2, Pl1, 200);
        Tau = Math.Clamp(Tau, 1, 256);
        Epp = Epp < 0 ? -1 : Math.Clamp(Epp, 0, 100);
        return this;
    }
}

public sealed class CpuSettings
{
    /// <summary>PC Monitor gère le processeur (remplace ThrottleStop).</summary>
    public bool Enabled { get; set; }
    /// <summary>Undervolt validé, en mV (négatif) : cœurs + cache, GPU Intel, System Agent.</summary>
    public int CoreMv { get; set; }
    public int GpuMv { get; set; }
    public int SaMv { get; set; }
    public Dictionary<string, CpuModeTune> Modes { get; set; } = new();
}

/// <summary>Accès aux registres MSR par PawnIO.</summary>
public static class Msr
{
    const uint IOCTL_LOAD = (41394u << 16) | (0x821 << 2);
    const uint IOCTL_EXEC = (41394u << 16) | (0x841 << 2);
    const int FnLen = 32;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    static readonly object Gate = new();
    static SafeFileHandle handle;
    static bool tried;

    public static string Error { get; private set; }

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                if (handle != null && !handle.IsInvalid) return true;
                if (tried) return false;
                tried = true;
                try
                {
                    var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (h.IsInvalid)
                    {
                        Error = "Pilote PawnIO introuvable : réinstalle PawnIO (PawnIO_setup.exe).";
                        Log.Info("PawnIO : pilote introuvable (" + Marshal.GetLastWin32Error() + ")");
                        return false;
                    }
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IntelMSR.bin");
                    using var m = new MemoryStream();
                    s.CopyTo(m);
                    var bin = m.ToArray();
                    if (!DeviceIoControl(h, IOCTL_LOAD, bin, bin.Length, null, 0, out _, IntPtr.Zero))
                    {
                        Error = "PawnIO refuse le module processeur : mets PawnIO à jour.";
                        Log.Info("PawnIO : chargement du module IntelMSR refusé (" + Marshal.GetLastWin32Error() + ")");
                        h.Dispose();
                        return false;
                    }
                    handle = h;
                    Error = null;
                    Log.Info("PawnIO : module processeur chargé");
                    return true;
                }
                catch (Exception e)
                {
                    Error = "Accès au processeur impossible (voir le journal).";
                    Log.Error("PawnIO", e);
                    return false;
                }
            }
        }
    }

    static bool Exec(string fn, long[] input, long[] output)
    {
        var inBuf = new byte[FnLen + input.Length * 8];
        Encoding.ASCII.GetBytes(fn, 0, Math.Min(fn.Length, FnLen - 1), inBuf, 0);
        Buffer.BlockCopy(input, 0, inBuf, FnLen, input.Length * 8);
        var outBuf = new byte[output.Length * 8];
        lock (Gate)
        {
            if (!Available) return false;
            if (!DeviceIoControl(handle, IOCTL_EXEC, inBuf, inBuf.Length, outBuf, outBuf.Length, out var read, IntPtr.Zero)) return false;
            Buffer.BlockCopy(outBuf, 0, output, 0, Math.Min(read, outBuf.Length));
            return true;
        }
    }

    public static bool Read(uint msr, out ulong value)
    {
        var o = new long[1];
        var ok = Exec("ioctl_read_msr", new long[] { msr }, o);
        value = (ulong)o[0];
        return ok;
    }

    public static bool Write(uint msr, ulong value) => Exec("ioctl_write_msr", new long[] { msr, (long)value }, Array.Empty<long>());

    public static void Close()
    {
        lock (Gate)
        {
            handle?.Dispose();
            handle = null;
        }
    }
}

public sealed class CpuTuner
{
    const uint PERF_STATUS = 0x198, PKG_THERM_STATUS = 0x1B1, TEMP_TARGET = 0x1A2, POWER_UNIT = 0x606,
        PKG_POWER_LIMIT = 0x610, OC_MAILBOX = 0x150, PLATFORM_INFO = 0xCE;

    public const int PlaneCore = 0, PlaneGpu = 1, PlaneCache = 2, PlaneSa = 3;

    readonly AppConfig cfg;
    readonly object gate = new();
    (int Core, int Gpu, int Sa)? test;
    Dictionary<string, object> uvCache;
    long uvCacheAt = -99999;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public CpuTuner(AppConfig cfg) => this.cfg = cfg;

    public static string CpuName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch { return null; }
    }

    public static bool IsIntel => (CpuName() ?? "").Contains("Intel", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- undervolt (boîte aux lettres OC, MSR 0x150)
    static bool Mailbox(ulong cmd, out ulong result)
    {
        result = 0;
        if (!Msr.Write(OC_MAILBOX, cmd)) return false;
        for (int i = 0; i < 20; i++)
        {
            if (!Msr.Read(OC_MAILBOX, out result)) return false;
            if ((result >> 63) == 0) return ((result >> 32) & 0xFF) == 0;
        }
        return false;
    }

    public static int? ReadOffset(int plane)
    {
        if (!Mailbox(0x8000001000000000UL | ((ulong)plane << 40), out var r)) return null;
        int raw = (int)((r >> 21) & 0x7FF);
        if (raw >= 0x400) raw -= 0x800;
        return (int)Math.Round(raw / 1.024);
    }

    static bool WriteOffset(int plane, int mv)
    {
        int raw = (int)Math.Round(mv * 1.024);
        ulong cmd = 0x8000001100000000UL | ((ulong)plane << 40) | ((ulong)(uint)(raw & 0x7FF) << 21);
        if (!Mailbox(cmd, out _)) return false;
        var back = ReadOffset(plane);
        return back.HasValue && Math.Abs(back.Value - mv) <= 2;
    }

    /// <summary>Applique un undervolt (cœurs et cache ensemble, comme le demandent les Intel de 8e génération).</summary>
    public (bool Ok, string Message) ApplyUndervolt(int core, int gpu, int sa)
    {
        core = Math.Clamp(core, -250, 0);
        gpu = Math.Clamp(gpu, -250, 0);
        sa = Math.Clamp(sa, -250, 0);
        lock (gate)
        {
            if (!Msr.Available) return (false, Msr.Error ?? "Accès au processeur impossible");
            var okCore = WriteOffset(PlaneCore, core);
            var okCache = WriteOffset(PlaneCache, core);
            var okGpu = WriteOffset(PlaneGpu, gpu);
            var okSa = WriteOffset(PlaneSa, sa);
            uvCacheAt = -99999;
            Log.Info($"Undervolt cœurs/cache {core} mV, GPU {gpu} mV, SA {sa} mV → {(okCore && okCache ? "OK" : "refusé")} / {okGpu} / {okSa}");
            if (!okCore || !okCache)
                return (false, core == 0 ? "Tension d'origine remise" : "Le processeur refuse l'undervolt : il est bloqué par le BIOS (protection Plundervolt) ou par l'isolation du noyau de Windows.");
            return (true, okGpu && okSa ? $"Undervolt appliqué : {core} mV" : $"Undervolt cœurs appliqué ({core} mV), GPU Intel ou System Agent refusé");
        }
    }

    public (bool Ok, string Message) TestUndervolt(int core, int gpu, int sa)
    {
        var r = ApplyUndervolt(core, gpu, sa);
        lock (gate) test = r.Ok ? (Math.Clamp(core, -250, 0), Math.Clamp(gpu, -250, 0), Math.Clamp(sa, -250, 0)) : null;
        return r.Ok ? (true, "En test : joue ou lance un test 10 à 15 min, puis appuie sur Garder") : r;
    }

    public (bool Ok, string Message) KeepTest()
    {
        lock (gate)
        {
            if (test == null) return (false, "Aucun réglage en test");
            var t = test.Value;
            lock (AppConfig.Gate)
            {
                cfg.Cpu.CoreMv = t.Core;
                cfg.Cpu.GpuMv = t.Gpu;
                cfg.Cpu.SaMv = t.Sa;
            }
            test = null;
        }
        cfg.Save();
        return (true, "Undervolt gardé : il sera appliqué à chaque démarrage");
    }

    public (bool Ok, string Message) CancelTest()
    {
        lock (gate) test = null;
        var r = ApplyUndervolt(cfg.Cpu.CoreMv, cfg.Cpu.GpuMv, cfg.Cpu.SaMv);
        return (r.Ok, r.Ok ? "Test annulé, réglages validés remis" : r.Message);
    }

    // ---------------------------------------------------------------- limites de puissance (MSR 0x610)
    static (double Watt, double Sec) Units()
    {
        if (!Msr.Read(POWER_UNIT, out var u)) return (0.125, 1.0 / 1024);
        return (1.0 / (1 << (int)(u & 0xF)), 1.0 / (1 << (int)((u >> 16) & 0xF)));
    }

    static double DecodeTau(ulong window7, double secUnit)
    {
        int y = (int)(window7 & 0x1F), z = (int)((window7 >> 5) & 0x3);
        return Math.Pow(2, y) * (1 + z / 4.0) * secUnit;
    }

    static ulong EncodeTau(double seconds, double secUnit)
    {
        ulong best = 0;
        double bestDiff = double.MaxValue;
        for (int y = 0; y < 32; y++)
            for (int z = 0; z < 4; z++)
            {
                var d = Math.Abs(Math.Pow(2, y) * (1 + z / 4.0) * secUnit - seconds);
                if (d < bestDiff) { bestDiff = d; best = (ulong)(y | (z << 5)); }
            }
        return best;
    }

    public static Dictionary<string, object> ReadLimits()
    {
        if (!Msr.Read(PKG_POWER_LIMIT, out var v)) return null;
        var (w, s) = Units();
        return new Dictionary<string, object>
        {
            ["pl1"] = Math.Round((v & 0x7FFF) * w, 1),
            ["pl2"] = Math.Round(((v >> 32) & 0x7FFF) * w, 1),
            ["pl1_on"] = ((v >> 15) & 1) == 1,
            ["pl2_on"] = ((v >> 47) & 1) == 1,
            ["tau"] = Math.Round(DecodeTau((v >> 17) & 0x7F, s), 1),
            ["locked"] = (v >> 63) == 1,
        };
    }

    public static (bool Ok, string Message) WriteLimits(int pl1, int pl2, int tau)
    {
        if (!Msr.Available) return (false, Msr.Error ?? "Accès au processeur impossible");
        if (!Msr.Read(PKG_POWER_LIMIT, out var v)) return (false, "Lecture des limites impossible");
        if ((v >> 63) == 1) return (false, "Limites de puissance verrouillées par le BIOS");
        var (w, s) = Units();
        ulong p1 = (ulong)Math.Round(pl1 / w) & 0x7FFF, p2 = (ulong)Math.Round(pl2 / w) & 0x7FFF;
        ulong n = v & ~(0x7FFFUL | (0x7FUL << 17) | (0x7FFFUL << 32));
        n |= p1 | (1UL << 15) | (EncodeTau(tau, s) << 17) | (p2 << 32) | (1UL << 47);
        if (!Msr.Write(PKG_POWER_LIMIT, n)) return (false, "Le processeur refuse les limites de puissance");
        Msr.Read(PKG_POWER_LIMIT, out var back);
        var ok = (back & 0x7FFF) == p1 && ((back >> 32) & 0x7FFF) == p2;
        Log.Info($"Limites PL1 {pl1} W, PL2 {pl2} W, {tau} s → {(ok ? "OK" : "non prises")}");
        return ok ? (true, $"Limites : PL1 {pl1} W, PL2 {pl2} W") : (false, "Le processeur n'a pas gardé les limites");
    }

    // ---------------------------------------------------------------- préférence d'énergie (Speed Shift, par Windows)
    public static void SetEpp(int percent)
    {
        const string sub = "54533251-82be-4824-96c1-47b60b740d00";  // SUB_PROCESSOR
        const string epp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";  // PERFEPP
        Win.Run("powercfg", $"/attributes {sub} {epp} -ATTRIB_HIDE", 5000);
        Win.Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {epp} {percent}", 5000);
        Win.Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {sub} {epp} {percent}", 5000);
        Win.Run("powercfg", "/setactive SCHEME_CURRENT", 5000);
    }

    // ---------------------------------------------------------------- application
    public CpuModeTune ModeTune(string mode)
    {
        lock (AppConfig.Gate)
        {
            if (!cfg.Cpu.Modes.TryGetValue(mode ?? "", out var t)) cfg.Cpu.Modes[mode ?? ""] = t = new CpuModeTune();
            return t;
        }
    }

    /// <summary>Réglages liés au mode : limites et préférence d'énergie (le Turbo est géré par ModeService).</summary>
    public void ApplyMode(string mode)
    {
        if (!cfg.Cpu.Enabled || string.IsNullOrEmpty(mode)) return;
        try
        {
            var t = ModeTune(mode);
            if (t.Limits) WriteLimits(t.Pl1, t.Pl2, t.Tau);
            if (t.Epp >= 0) SetEpp(t.Epp);
        }
        catch (Exception e)
        {
            Log.Error("Réglages processeur du mode", e);
        }
    }

    /// <summary>Au démarrage et au réveil : undervolt validé puis réglages du mode.</summary>
    public void Reapply()
    {
        if (!cfg.Cpu.Enabled) return;
        try
        {
            (int Core, int Gpu, int Sa) uv;
            lock (gate) uv = test ?? (cfg.Cpu.CoreMv, cfg.Cpu.GpuMv, cfg.Cpu.SaMv);
            if (uv.Core != 0 || uv.Gpu != 0 || uv.Sa != 0) ApplyUndervolt(uv.Core, uv.Gpu, uv.Sa);
            ApplyMode(cfg.LastMode);
        }
        catch (Exception e)
        {
            Log.Error("Réapplication des réglages processeur", e);
        }
    }

    public static bool ThrottleStopRunning() => Process.GetProcessesByName("ThrottleStop").Length > 0;

    /// <summary>Reprend les réglages que ThrottleStop a appliqués (lus dans le processeur), puis le ferme et coupe son lancement automatique.</summary>
    public (bool Ok, string Message) TakeOver()
    {
        if (!Msr.Available) return (false, Msr.Error ?? "Accès au processeur impossible");
        var notes = new List<string>();
        var core = ReadOffset(PlaneCore);
        var cache = ReadOffset(PlaneCache);
        var gpu = ReadOffset(PlaneGpu);
        var sa = ReadOffset(PlaneSa);
        var lim = ReadLimits();
        lock (AppConfig.Gate)
        {
            if (core.HasValue)
            {
                cfg.Cpu.CoreMv = Math.Clamp(Math.Max(core.Value, cache ?? core.Value), -250, 0);
                cfg.Cpu.GpuMv = Math.Clamp(gpu ?? 0, -250, 0);
                cfg.Cpu.SaMv = Math.Clamp(sa ?? 0, -250, 0);
                notes.Add($"undervolt {cfg.Cpu.CoreMv} mV");
            }
            if (lim != null && !(bool)lim["locked"])
            {
                int pl1 = (int)Math.Round((double)lim["pl1"]), pl2 = (int)Math.Round((double)lim["pl2"]), tau = (int)Math.Round((double)lim["tau"]);
                foreach (var m in ModeDefs.Order)
                {
                    if (!cfg.Cpu.Modes.TryGetValue(m, out var t)) cfg.Cpu.Modes[m] = t = new CpuModeTune();
                    t.Limits = true;
                    t.Pl1 = pl1;
                    t.Pl2 = Math.Max(pl1, pl2);
                    t.Tau = Math.Max(1, tau);
                    t.Clamp();
                }
                notes.Add($"PL1 {pl1} W / PL2 {pl2} W");
            }
            cfg.Cpu.Enabled = true;
            cfg.LaunchWith.RemoveAll(p => Path.GetFileName(p).StartsWith("ThrottleStop", StringComparison.OrdinalIgnoreCase));
        }
        cfg.Save();

        foreach (var p in Process.GetProcessesByName("ThrottleStop"))
        {
            try { p.Kill(); notes.Add("ThrottleStop fermé"); } catch (Exception e) { Log.Error("Fermeture de ThrottleStop", e); }
            finally { p.Dispose(); }
        }
        // tâches planifiées qui lancent ThrottleStop au démarrage
        try
        {
            var list = Win.Decode(Win.Run("schtasks", "/query /fo csv /nh", 15000).Output) ?? "";
            foreach (var line in list.Split('\n'))
            {
                var name = line.Split(',').FirstOrDefault()?.Trim().Trim('"');
                if (string.IsNullOrEmpty(name) || !name.Contains("ThrottleStop", StringComparison.OrdinalIgnoreCase)) continue;
                Win.Run("schtasks", $"/change /tn \"{name}\" /disable", 10000);
                notes.Add($"tâche « {name.TrimStart('\\')} » désactivée");
            }
        }
        catch (Exception e) { Log.Error("Tâches ThrottleStop", e); }
        // raccourci dans le dossier Démarrage
        try
        {
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                     })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var lnk in Directory.GetFiles(dir, "*ThrottleStop*"))
                {
                    var off = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? dir, Path.GetFileName(lnk) + ".desactive");
                    File.Move(lnk, off, true);
                    notes.Add("raccourci de démarrage retiré");
                }
            }
        }
        catch (Exception e) { Log.Error("Raccourci ThrottleStop", e); }
        // registre (lancement à l'ouverture de session)
        try
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (run == null) continue;
                foreach (var n in run.GetValueNames())
                    if (n.Contains("ThrottleStop", StringComparison.OrdinalIgnoreCase) || (run.GetValue(n) as string ?? "").Contains("ThrottleStop", StringComparison.OrdinalIgnoreCase))
                    {
                        run.DeleteValue(n, false);
                        notes.Add("lancement automatique retiré");
                    }
            }
        }
        catch (Exception e) { Log.Error("Registre ThrottleStop", e); }

        Log.Info("Reprise de ThrottleStop : " + string.Join(", ", notes));
        Reapply();
        return (true, "Repris : " + (notes.Count > 0 ? string.Join(", ", notes) : "aucun réglage trouvé"));
    }

    // ---------------------------------------------------------------- état pour le téléphone
    public Dictionary<string, object> State()
    {
        var d = new Dictionary<string, object>
        {
            ["name"] = CpuName(),
            ["intel"] = IsIntel,
            ["throttlestop"] = ThrottleStopRunning(),
        };
        lock (AppConfig.Gate)
        {
            d["settings"] = new Dictionary<string, object>
            {
                ["enabled"] = cfg.Cpu.Enabled,
                ["core"] = cfg.Cpu.CoreMv,
                ["gpu"] = cfg.Cpu.GpuMv,
                ["sa"] = cfg.Cpu.SaMv,
                ["modes"] = ModeDefs.Order.ToDictionary(m => m, m =>
                {
                    cfg.Cpu.Modes.TryGetValue(m, out var t);
                    t ??= new CpuModeTune();
                    cfg.CpuBoost.TryGetValue(m, out var boost);
                    return (object)new Dictionary<string, object>
                    {
                        ["limits"] = t.Limits, ["pl1"] = t.Pl1, ["pl2"] = t.Pl2, ["tau"] = t.Tau, ["epp"] = t.Epp, ["boost"] = boost,
                    };
                }),
            };
            d["mode"] = cfg.LastMode;
        }
        lock (gate) d["test"] = test.HasValue ? new Dictionary<string, object> { ["core"] = test.Value.Core, ["gpu"] = test.Value.Gpu, ["sa"] = test.Value.Sa } : null;

        if (!IsIntel || !Msr.Available)
        {
            d["available"] = false;
            d["error"] = !IsIntel ? "Ces réglages ne concernent que les processeurs Intel." : Msr.Error;
            return d;
        }
        d["available"] = true;
        if (Msr.Read(PERF_STATUS, out var ps))
        {
            d["ratio"] = (int)((ps >> 8) & 0xFF);
            var vid = ((ps >> 32) & 0xFFFF) / 8192.0;
            d["vid"] = vid > 0.3 && vid < 2 ? (object)Math.Round(vid, 3) : null;
        }
        if (Msr.Read(PKG_THERM_STATUS, out var th))
            d["throttle"] = new Dictionary<string, object>
            {
                ["thermal"] = (th & 1) == 1,
                ["prochot"] = ((th >> 2) & 1) == 1,
                ["power"] = ((th >> 10) & 1) == 1,
            };
        if (Msr.Read(TEMP_TARGET, out var tt)) d["tjmax"] = (int)((tt >> 16) & 0xFF);
        if (Msr.Read(PLATFORM_INFO, out var pi)) d["base_ratio"] = (int)((pi >> 8) & 0xFF);
        d["limits"] = ReadLimits();
        // l'undervolt réel se lit par la boîte aux lettres : pas plus d'une fois toutes les 5 s
        lock (gate)
        {
            if (Clock.ElapsedMilliseconds - uvCacheAt > 5000)
            {
                uvCache = new Dictionary<string, object>
                {
                    ["core"] = ReadOffset(PlaneCore), ["cache"] = ReadOffset(PlaneCache), ["gpu"] = ReadOffset(PlaneGpu), ["sa"] = ReadOffset(PlaneSa),
                };
                uvCacheAt = Clock.ElapsedMilliseconds;
            }
            d["uv"] = uvCache;
        }
        return d;
    }
}
