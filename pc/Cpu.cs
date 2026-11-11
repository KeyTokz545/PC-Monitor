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
    /// <summary>Ancienne préférence d'énergie en % (versions 2.3-2.4), convertie en EppRaw.</summary>
    public int Epp { get; set; } = -1;
    /// <summary>Préférence d'énergie Speed Shift sur l'échelle de ThrottleStop : 0 (perf) à 255 (économie), -1 = laisser Windows.</summary>
    public int EppRaw { get; set; } = -1;
    /// <summary>Fréquence maximale en MHz (0 = pas de limite).</summary>
    public int MaxMhz { get; set; }
    /// <summary>Undervolt validé de ce profil, en mV : cœurs + cache, GPU Intel, System Agent.</summary>
    public int CoreMv { get; set; }
    public int GpuMv { get; set; }
    public int SaMv { get; set; }
    /// <summary>Turbo Boost de Windows : 0 coupé, 1 activé, 2 agressif, -1 = ne pas toucher.</summary>
    public int Boost { get; set; } = -1;

    public CpuModeTune Clamp()
    {
        Pl1 = Math.Clamp(Pl1, 5, 150);
        Pl2 = Math.Clamp(Pl2, Pl1, 200);
        Tau = Math.Clamp(Tau, 1, 256);
        if (EppRaw < 0 && Epp >= 0) EppRaw = (int)Math.Round(Math.Clamp(Epp, 0, 100) * 2.55);
        Epp = -1;
        EppRaw = EppRaw < 0 ? -1 : Math.Clamp(EppRaw, 0, 255);
        MaxMhz = MaxMhz <= 0 ? 0 : Math.Clamp(MaxMhz, 800, 6000);
        CoreMv = Math.Clamp(CoreMv, -250, 0);
        GpuMv = Math.Clamp(GpuMv, -250, 0);
        SaMv = Math.Clamp(SaMv, -250, 0);
        Boost = Boost < 0 ? -1 : Math.Clamp(Boost, 0, 2);
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
    /// <summary>
    /// Seuil thermique à TjMax (100 °C) : le module PawnIO ne permet pas de l'écrire, donc on lance ThrottleStop
    /// caché quelques secondes au démarrage pour qu'il le règle, puis on le ferme.
    /// </summary>
    public bool TsBoot { get; set; }
    public string TsPath { get; set; }
    /// <summary>Les 3 profils de Lilian (Léger, Médium, Gros) ont été chargés une fois.</summary>
    public bool Profiles3 { get; set; }
    /// <summary>L'undervolt commun (2.3-2.5) a été recopié dans chaque profil.</summary>
    public bool UvPerMode { get; set; }
    /// <summary>Profil CPU actif (silent = Léger, balanced = Médium, turbo = Gros), indépendant de la ventilation.</summary>
    public string Active { get; set; }
    /// <summary>Le profil CPU suit le mode de ventilation (comme en 2.5-2.6).</summary>
    public bool Linked { get; set; }
    /// <summary>Le Turbo par mode de ventilation (CpuBoost) a été recopié dans les profils CPU.</summary>
    public bool BoostPerProfile { get; set; }

    public void MigrateBoost(Dictionary<string, int> boost, string lastMode)
    {
        if (string.IsNullOrEmpty(Active) || !ModeDefs.Known(Active)) Active = ModeDefs.Known(lastMode) ? lastMode : "balanced";
        if (BoostPerProfile) return;
        foreach (var m in ModeDefs.Order)
        {
            if (!Modes.TryGetValue(m, out var t) || t == null) Modes[m] = t = new CpuModeTune();
            t.Boost = boost.TryGetValue(m, out var b) ? b : 2;
        }
        BoostPerProfile = true;
    }

    public void MigrateUndervolt()
    {
        if (UvPerMode) return;
        foreach (var m in ModeDefs.Order)
        {
            if (!Modes.TryGetValue(m, out var t) || t == null) Modes[m] = t = new CpuModeTune();
            t.CoreMv = CoreMv;
            t.GpuMv = GpuMv;
            t.SaMv = SaMv;
        }
        UvPerMode = true;
    }

    /// <summary>
    /// Profils repris de ThrottleStop : Léger (Silencieux), Médium 3,2 GHz / 50 W (Équilibré),
    /// Gros 4,1 GHz / 75 W + seuil 100 °C (Turbo). Tout reste modifiable dans l'onglet CPU.
    /// </summary>
    public void LoadDefaultProfiles(Dictionary<string, int> boost)
    {
        Modes["silent"] = new CpuModeTune { Limits = true, Pl1 = 25, Pl2 = 25, Tau = 28, EppRaw = 128, MaxMhz = 2400, Boost = 1 };
        Modes["balanced"] = new CpuModeTune { Limits = true, Pl1 = 50, Pl2 = 50, Tau = 28, EppRaw = 16, MaxMhz = 3200, Boost = 2 };
        Modes["turbo"] = new CpuModeTune { Limits = true, Pl1 = 75, Pl2 = 75, Tau = 28, EppRaw = 16, MaxMhz = 0, Boost = 2 };
        boost["silent"] = 1;
        boost["balanced"] = 2;
        boost["turbo"] = 2;
        TsBoot = true;
        Profiles3 = true;
    }
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
    (string Mode, int Core, int Gpu, int Sa)? test;

    /// <summary>Undervolt à appliquer pour un mode : celui en test s'il concerne ce mode, sinon celui du profil.</summary>
    (int Core, int Gpu, int Sa) UvFor(string mode)
    {
        lock (gate)
        {
            if (test.HasValue && test.Value.Mode == mode) return (test.Value.Core, test.Value.Gpu, test.Value.Sa);
        }
        var t = ModeTune(mode);
        return (t.CoreMv, t.GpuMv, t.SaMv);
    }
    Dictionary<string, object> uvCache;
    long uvCacheAt = -99999;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    System.Threading.Timer timer;
    int ticks;

    public CpuTuner(AppConfig cfg) => this.cfg = cfg;

    /// <summary>Toutes les 10 s : remet les limites (le BIOS ASUS les réécrit parfois) et l'undervolt (perdu après certaines veilles).</summary>
    public void Start() => timer = new System.Threading.Timer(_ => Maintain(), null, 15000, 10000);

    public void Stop() => timer?.Dispose();

    void Maintain()
    {
        try
        {
            if (!cfg.Cpu.Enabled || !Msr.Available || ThrottleStopRunning()) return;
            var t = ModeTune(cfg.Cpu.Active);
            if (t.Limits && Msr.Read(PKG_POWER_LIMIT, out var v) && (v >> 63) == 0)
            {
                var (w, _) = Units();
                if (Math.Abs((v & 0x7FFF) * w - t.Pl1) > 1 || Math.Abs(((v >> 32) & 0x7FFF) * w - t.Pl2) > 1)
                {
                    Log.Info("Limites de puissance modifiées par le système : remises");
                    WriteLimits(t.Pl1, t.Pl2, t.Tau);
                }
            }
            if (++ticks % 3 != 0) return;
            if (cfg.Cpu.TsBoot && TccOffset() > 0) System.Threading.Tasks.Task.Run(() => EnsureThreshold());
            var uv = UvFor(cfg.Cpu.Active);
            if (uv.Core == 0) return;
            var now = ReadOffset(PlaneCore);
            if (now.HasValue && Math.Abs(now.Value - uv.Core) > 2)
            {
                Log.Info($"Undervolt perdu ({now} mV au lieu de {uv.Core}) : remis");
                ApplyUndervolt(uv.Core, uv.Gpu, uv.Sa);
            }
        }
        catch (Exception e)
        {
            Log.Error("Entretien des réglages processeur", e);
        }
    }

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

    /// <summary>Test d'undervolt pour un profil. Le mode du profil doit être actif (ModeService s'en charge avant).</summary>
    public (bool Ok, string Message) TestUndervolt(string mode, int core, int gpu, int sa)
    {
        core = Math.Clamp(core, -250, 0);
        gpu = Math.Clamp(gpu, -250, 0);
        sa = Math.Clamp(sa, -250, 0);
        lock (gate) test = (mode, core, gpu, sa);
        var r = ApplyUndervolt(core, gpu, sa);
        if (!r.Ok) lock (gate) test = null;
        return r.Ok ? (true, "En test : joue ou lance un test 10 à 15 min, puis appuie sur Garder") : r;
    }

    public (bool Ok, string Message) KeepTest()
    {
        string mode;
        lock (gate)
        {
            if (test == null) return (false, "Aucun réglage en test");
            var t = test.Value;
            mode = t.Mode;
            var p = ModeTune(t.Mode);
            lock (AppConfig.Gate)
            {
                p.CoreMv = t.Core;
                p.GpuMv = t.Gpu;
                p.SaMv = t.Sa;
            }
            test = null;
        }
        cfg.Save();
        return (true, $"Undervolt gardé pour le profil {ProfileLabel(mode)}");
    }

    public (bool Ok, string Message) CancelTest()
    {
        lock (gate) test = null;
        var uv = UvFor(cfg.Cpu.Active);
        var r = ApplyUndervolt(uv.Core, uv.Gpu, uv.Sa);
        return (r.Ok, r.Ok ? "Test annulé, undervolt du profil remis" : r.Message);
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
    /// <summary>
    /// Préférence d'énergie (Speed Shift) et fréquence max par le mode d'alimentation de Windows, qui les transmet au processeur.
    /// EPP sur l'échelle ThrottleStop 0-255 (Windows l'exprime en %), fréquence en MHz (0 = pas de limite).
    /// </summary>
    public static void ApplyPowerPlan(int eppRaw, int maxMhz, int boost = -1)
    {
        const string boostId = "be337238-0d82-4146-a960-4f3749d470c7"; // PERFBOOSTMODE
        if (boost >= 0)
        {
            Win.Run("powercfg", $"/setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 {boostId} {boost}", 5000);
            Win.Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 {boostId} {boost}", 5000);
        }
        const string sub = "54533251-82be-4824-96c1-47b60b740d00";   // SUB_PROCESSOR
        const string epp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";   // PERFEPP
        const string fmax = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";  // PROCFREQMAX
        if (eppRaw >= 0)
        {
            var pct = (int)Math.Round(eppRaw / 2.55);
            Win.Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {epp} {pct}", 5000);
            Win.Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {sub} {epp} {pct}", 5000);
        }
        Win.Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {fmax} {Math.Max(0, maxMhz)}", 5000);
        Win.Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {sub} {fmax} {Math.Max(0, maxMhz)}", 5000);
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

    public static string ProfileLabel(string id) => id switch { "silent" => "Léger", "balanced" => "Médium", "turbo" => "Gros", _ => id };

    /// <summary>Le profil CPU a changé (pour l'icône près de l'horloge).</summary>
    public event Action<string> ProfileChanged;

    /// <summary>Choisit le profil CPU actif, indépendamment de la ventilation.</summary>
    public (bool Ok, string Message) SetProfile(string id, bool save = true)
    {
        if (!ModeDefs.Known(id)) return (false, "Profil CPU inconnu");
        cfg.Cpu.Active = id;
        if (save) cfg.Save();
        if (!cfg.Cpu.Enabled) return (true, $"Profil {ProfileLabel(id)} choisi (active « PC Monitor gère le processeur » pour l'appliquer)");
        ApplyProfile(id);
        try { ProfileChanged?.Invoke(id); } catch { }
        return (true, $"Profil CPU {ProfileLabel(id)}");
    }

    /// <summary>Changement de ventilation : profil lié s'il est demandé, sinon on réaffirme le profil actif (le BIOS peut toucher aux limites).</summary>
    public void OnFanModeChanged(string mode)
    {
        if (!cfg.Cpu.Enabled) return;
        if (cfg.Cpu.Linked && ModeDefs.Known(mode) && mode != cfg.Cpu.Active) SetProfile(mode);
        else ApplyProfile(cfg.Cpu.Active);
    }

    /// <summary>Applique un profil CPU : undervolt, limites, fréquence max, EPP, Turbo.</summary>
    public void ApplyProfile(string mode)
    {
        if (!cfg.Cpu.Enabled || string.IsNullOrEmpty(mode)) return;
        try
        {
            var t = ModeTune(mode);
            // un test en cours sur un autre profil est abandonné
            lock (gate)
                if (test.HasValue && test.Value.Mode != mode) test = null;
            var uv = UvFor(mode);
            if (Msr.Available) ApplyUndervolt(uv.Core, uv.Gpu, uv.Sa);
            if (t.Limits) WriteLimits(t.Pl1, t.Pl2, t.Tau);
            ApplyPowerPlan(t.EppRaw, t.MaxMhz, t.Boost);
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
            ApplyProfile(cfg.Cpu.Active);
        }
        catch (Exception e)
        {
            Log.Error("Réapplication des réglages processeur", e);
        }
    }

    public static bool ThrottleStopRunning()
    {
        var list = Process.GetProcessesByName("ThrottleStop");
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    /// <summary>Décalage du seuil thermique (TCC offset) en °C : 0 = le processeur ne ralentit qu'à TjMax.</summary>
    public static int? TccOffset() => Msr.Read(TEMP_TARGET, out var v) ? (int)((v >> 24) & 0x3F) : null;

    string ThrottleStopPath()
    {
        var p = cfg.Cpu.TsPath;
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        p = Catalog.FindTools().FirstOrDefault(t => Path.GetFileName(t.Path).StartsWith("ThrottleStop", StringComparison.OrdinalIgnoreCase)).Path;
        if (!string.IsNullOrEmpty(p) && File.Exists(p))
        {
            cfg.Cpu.TsPath = p;
            cfg.Save();
            return p;
        }
        return null;
    }

    long lastTs = -999_999;
    public string TsStatus { get; private set; }

    /// <summary>
    /// Si le seuil n'est plus à TjMax (redémarrage, veille), lance ThrottleStop caché, attend qu'il ait réglé le seuil,
    /// le ferme et remet les réglages de PC Monitor par-dessus. Au plus une fois toutes les 10 minutes.
    /// </summary>
    public void EnsureThreshold(bool force = false)
    {
        try
        {
            if (!cfg.Cpu.Enabled || !cfg.Cpu.TsBoot || !Msr.Available) return;
            if (TccOffset() == 0 || ThrottleStopRunning()) return;
            if (!force && Clock.ElapsedMilliseconds - lastTs < 600_000) return;
            lastTs = Clock.ElapsedMilliseconds;
            var path = ThrottleStopPath();
            if (path == null)
            {
                TsStatus = "ThrottleStop introuvable : garde-le installé (dossier d'origine) pour régler le seuil à 100 °C.";
                Log.Info("Seuil thermique : ThrottleStop introuvable");
                return;
            }
            using var p = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path),
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            int? off = null;
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(500);
                off = TccOffset();
                if (off == 0) break;
            }
            System.Threading.Thread.Sleep(1500); // le temps qu'il finisse d'appliquer le reste
            foreach (var q in Process.GetProcessesByName("ThrottleStop"))
            {
                try { q.Kill(); } catch { }
                finally { q.Dispose(); }
            }
            TsStatus = off == 0 ? null : "ThrottleStop n'a pas réglé le seuil : vérifie le PROCHOT Offset (0) dans ThrottleStop.";
            Log.Info($"Seuil thermique via ThrottleStop : décalage {off} °C");
            Win.Run("powercfg", "/setactive SCHEME_CURRENT", 5000); // Windows remet sa préférence d'énergie
            Reapply();
        }
        catch (Exception e)
        {
            Log.Error("Seuil thermique via ThrottleStop", e);
        }
    }

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
        var tcc = TccOffset();
        foreach (var p in Process.GetProcessesByName("ThrottleStop"))
        {
            try { cfg.Cpu.TsPath = p.MainModule?.FileName ?? cfg.Cpu.TsPath; } catch { }
            finally { p.Dispose(); }
        }
        lock (AppConfig.Gate)
        {
            if (tcc == 0)
            {
                // ThrottleStop avait mis le seuil à TjMax : on le garde grâce au lancement éclair au démarrage
                cfg.Cpu.TsBoot = true;
                notes.Add("seuil 100 °C gardé");
            }
            if (core.HasValue)
            {
                int c = Math.Clamp(Math.Max(core.Value, cache ?? core.Value), -250, 0), g = Math.Clamp(gpu ?? 0, -250, 0), a = Math.Clamp(sa ?? 0, -250, 0);
                foreach (var m in ModeDefs.Order)
                {
                    if (!cfg.Cpu.Modes.TryGetValue(m, out var mt) || mt == null) cfg.Cpu.Modes[m] = mt = new CpuModeTune();
                    mt.CoreMv = c;
                    mt.GpuMv = g;
                    mt.SaMv = a;
                }
                notes.Add($"undervolt {c} mV pour les 3 profils");
            }
            if (lim != null && !(bool)lim["locked"] && !cfg.Cpu.Profiles3)
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

                ["ts_boot"] = cfg.Cpu.TsBoot,
                ["ts_found"] = !string.IsNullOrEmpty(cfg.Cpu.TsPath) && File.Exists(cfg.Cpu.TsPath),
                ["ts_status"] = TsStatus,
                ["active"] = cfg.Cpu.Active,
                ["linked"] = cfg.Cpu.Linked,
                ["modes"] = ModeDefs.Order.ToDictionary(m => m, m =>
                {
                    cfg.Cpu.Modes.TryGetValue(m, out var t);
                    t ??= new CpuModeTune();
                    return (object)new Dictionary<string, object>
                    {
                        ["limits"] = t.Limits, ["pl1"] = t.Pl1, ["pl2"] = t.Pl2, ["tau"] = t.Tau, ["epp"] = t.EppRaw, ["max_mhz"] = t.MaxMhz, ["boost"] = t.Boost,
                        ["core"] = t.CoreMv, ["gpu"] = t.GpuMv, ["sa"] = t.SaMv,
                    };
                }),
            };
            d["mode"] = cfg.LastMode;
        }
        lock (gate) d["test"] = test.HasValue ? new Dictionary<string, object> { ["mode"] = test.Value.Mode, ["core"] = test.Value.Core, ["gpu"] = test.Value.Gpu, ["sa"] = test.Value.Sa } : null;

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
        if (Msr.Read(TEMP_TARGET, out var tt))
        {
            d["tjmax"] = (int)((tt >> 16) & 0xFF);
            d["tcc_offset"] = (int)((tt >> 24) & 0x3F);
        }
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
