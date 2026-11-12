// Profils de jeu automatiques : quand un jeu se lance, PC Monitor applique ses réglages (mode, bridage GPU,
// couleurs, touche Windows) puis remet tout comme avant quand le jeu se ferme.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCMonitor;

public sealed class GameProfile
{
    /// <summary>Nom du programme, ex. cod.exe.</summary>
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Mode de performance pendant le jeu (null = ne pas toucher).</summary>
    public string Mode { get; set; }
    /// <summary>Bridage GPU pendant le jeu (null = ne pas toucher).</summary>
    public bool? Gpu { get; set; }
    /// <summary>Profil de couleur pendant le jeu (null = ne pas toucher).</summary>
    public string Color { get; set; }
    /// <summary>Bloquer la touche Windows pendant le jeu.</summary>
    public bool WinKey { get; set; }
    /// <summary>Profil CPU pendant le jeu (null = ne pas toucher), indépendant de la ventilation.</summary>
    public string CpuProfile { get; set; }

    public static string Norm(string exe)
    {
        exe = (exe ?? "").Trim().Trim('"');
        var slash = exe.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) exe = exe[(slash + 1)..];
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
        return exe.ToLowerInvariant();
    }
}

/// <summary>Réglages partagés par le téléphone et les profils de jeu.</summary>
public static class Tweaks
{
    public static (bool Ok, string Message) SetGpu(AppConfig cfg, bool on, int mhz = -1)
    {
        if (mhz < 0) mhz = cfg.GpuLimit.Mhz;
        mhz = Math.Clamp(mhz, 300, 3000);
        var (ok, msg) = Gpu.SetLimit(on ? mhz : 0);
        if (ok)
        {
            cfg.GpuLimit.On = on;
            cfg.GpuLimit.Mhz = mhz;
            cfg.Save();
        }
        return (ok, msg);
    }

    /// <summary>Applique le décalage de courbe GPU (cœur/mémoire). save=false pour un essai non enregistré.</summary>
    public static (bool Ok, string Message) SetGpuTune(AppConfig cfg, bool on, int core, int mem, bool save)
    {
        core = Math.Clamp(core, -500, 1000);
        mem = Math.Clamp(mem, -2000, 3000);
        var (ok, msg) = on ? Gpu.SetOffsets(core, mem) : Gpu.ClearOffsets();
        if (ok && save)
        {
            cfg.GpuLimit.TuneOn = on;
            cfg.GpuLimit.CoreOffset = core;
            cfg.GpuLimit.MemOffset = mem;
            cfg.Save();
        }
        return (ok, msg);
    }

    /// <summary>Réapplique le réglage GPU gardé (décalage + plafond) au démarrage / après veille.</summary>
    public static void ReapplyGpu(AppConfig cfg)
    {
        try
        {
            if (cfg.GpuLimit.TuneOn && Gpu.TuneSupported)
                Gpu.SetOffsets(cfg.GpuLimit.CoreOffset, cfg.GpuLimit.MemOffset);
            if (cfg.GpuLimit.On)
                Gpu.SetLimit(cfg.GpuLimit.Mhz);
        }
        catch (Exception e) { Log.Error("Réapplication GPU", e); }
    }

    public static ColorProfile FindColor(AppConfig cfg, string name)
    {
        lock (AppConfig.Gate)
            return cfg.ColorProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Copy();
    }

    public static (bool Ok, string Message) SetColor(AppConfig cfg, string name)
    {
        var p = FindColor(cfg, name);
        if (p == null) return (false, "Profil de couleur introuvable");
        var (ok, msg) = ScreenColor.Apply(p);
        cfg.ActiveColor = p.Name;
        cfg.Save();
        return (ok, msg);
    }

    public static (bool Ok, string Message) SetWinKey(AppConfig cfg, bool on)
    {
        var ok = WinKey.Set(on);
        if (ok)
        {
            cfg.WinKeyLock = on;
            cfg.Save();
        }
        return (ok, ok ? (on ? "Touche Windows bloquée" : "Touche Windows débloquée") : "Impossible de bloquer la touche Windows");
    }

    // ---------------------------------------------------------------- minuteur d'arrêt / de veille
    static CancellationTokenSource timerCts;
    public static DateTime? TimerAt { get; private set; }
    public static string TimerWhat { get; private set; }

    public static (bool Ok, string Message) SetTimer(int minutes, string what)
    {
        timerCts?.Cancel();
        timerCts = null;
        TimerAt = null;
        if (minutes <= 0) return (true, "Minuteur annulé");
        what = what == "sleep" ? "sleep" : "shutdown";
        var cts = new CancellationTokenSource();
        timerCts = cts;
        TimerAt = DateTime.Now.AddMinutes(minutes);
        TimerWhat = what;
        Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            TimerAt = null;
            Log.Info("Minuteur : " + (what == "sleep" ? "mise en veille" : "arrêt"));
            if (what == "sleep") Win.Sleep();
            else Win.Run("shutdown", "/s /t 30 /c \"PC Monitor : arrêt programmé\"", 5000);
        });
        var h = minutes >= 60 ? $"{minutes / 60} h{(minutes % 60 > 0 ? $" {minutes % 60:00}" : "")}" : $"{minutes} min";
        return (true, (what == "sleep" ? "Mise en veille" : "Arrêt") + $" dans {h}");
    }
}

public sealed class GameWatcher
{
    readonly AppConfig cfg;
    readonly ModeService modes;
    readonly CpuTuner cpu;
    readonly object gate = new();

    /// <summary>Mesures pour le bilan de partie (fourni par TrayApp).</summary>
    public Collector Collector { get; set; }
    float cpuMax, gpuMax;
    double cpuSum, gpuSum;
    int cpuN, gpuN;
    Timer timer;
    int ticks;
    GameProfile active;
    DateTime since;
    string beforeMode, beforeColor, beforeCpu;
    bool beforeGpu, beforeWinKey;

    public GameWatcher(AppConfig cfg, ModeService modes, CpuTuner cpu)
    {
        this.cfg = cfg;
        this.modes = modes;
        this.cpu = cpu;
    }

    public string ActiveExe => active?.Exe;
    public string ActiveName => active?.Name;
    public DateTime? Since => active != null ? since : null;

    public void Start() => timer = new Timer(_ => Check(), null, 6000, 3000);

    public void Stop()
    {
        timer?.Dispose();
        lock (gate)
            if (active != null) Restore();
    }

    void Check()
    {
        try
        {
            // les couleurs peuvent être remises à zéro par Windows (veille, jeu en plein écran…)
            if (++ticks % 7 == 0) ScreenColor.Reapply();

            List<GameProfile> list;
            lock (AppConfig.Gate) list = cfg.Games.Where(g => g.Enabled && !string.IsNullOrWhiteSpace(g.Exe)).ToList();
            if (list.Count == 0 && active == null) return;

            var running = new Dictionary<string, int>();
            foreach (var p in Process.GetProcesses())
            {
                try { running.TryAdd(p.ProcessName.ToLowerInvariant(), p.Id); }
                catch { }
                finally { p.Dispose(); }
            }

            lock (gate)
            {
                if (active != null)
                {
                    if (running.TryGetValue(GameProfile.Norm(active.Exe), out var pid))
                    {
                        Fps.PreferredPid = pid;
                        if (Collector?.CpuTemp is float ct) { cpuMax = Math.Max(cpuMax, ct); cpuSum += ct; cpuN++; }
                        if (Collector?.GpuTemp is float gt && gt > 1) { gpuMax = Math.Max(gpuMax, gt); gpuSum += gt; gpuN++; }
                    }
                    else Restore();
                    return;
                }
                foreach (var g in list)
                    if (running.TryGetValue(GameProfile.Norm(g.Exe), out var pid))
                    {
                        Activate(g, pid);
                        return;
                    }
            }
        }
        catch (Exception e)
        {
            Log.Error("Profils de jeu", e);
        }
    }

    void Activate(GameProfile g, int pid)
    {
        active = g;
        since = DateTime.Now;
        beforeMode = cfg.LastMode;
        beforeGpu = cfg.GpuLimit.On;
        beforeColor = cfg.ActiveColor;
        beforeWinKey = cfg.WinKeyLock;
        beforeCpu = cfg.Cpu.Active;
        Fps.PreferredPid = pid;
        cpuMax = gpuMax = 0;
        cpuSum = gpuSum = 0;
        cpuN = gpuN = 0;
        try { Fps.BeginSession(); } catch (Exception e) { Log.Error("Mesure des FPS de la partie", e); }
        Log.Info($"Jeu détecté : {g.Name} ({g.Exe})");
        if (!string.IsNullOrEmpty(g.Mode) && ModeDefs.Known(g.Mode) && g.Mode != cfg.LastMode) modes.Apply(g.Mode);
        if (g.Gpu.HasValue && g.Gpu.Value != cfg.GpuLimit.On) Tweaks.SetGpu(cfg, g.Gpu.Value);
        if (!string.IsNullOrEmpty(g.Color) && !string.Equals(g.Color, cfg.ActiveColor, StringComparison.OrdinalIgnoreCase)) Tweaks.SetColor(cfg, g.Color);
        if (g.WinKey && !cfg.WinKeyLock) Tweaks.SetWinKey(cfg, true);
        if (ModeDefs.Known(g.CpuProfile) && g.CpuProfile != cfg.Cpu.Active) cpu.SetProfile(g.CpuProfile);
    }

    void Restore()
    {
        var g = active;
        active = null;
        Dictionary<string, object> fps = null;
        try { fps = Fps.EndSession(); } catch (Exception e) { Log.Error("Fin de mesure des FPS", e); }
        Fps.PreferredPid = 0;
        var secs = (long)(DateTime.Now - since).TotalSeconds;
        if (secs >= 60)
        {
            var r = new Dictionary<string, object>
            {
                ["id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["game"] = g.Name,
                ["exe"] = g.Exe,
                ["start"] = since.ToString("dd/MM HH:mm"),
                ["duration"] = secs,
                ["cpu_max"] = cpuN > 0 ? (object)(int)Math.Round(cpuMax) : null,
                ["cpu_avg"] = cpuN > 0 ? (object)(int)Math.Round(cpuSum / cpuN) : null,
                ["gpu_max"] = gpuN > 0 ? (object)(int)Math.Round(gpuMax) : null,
                ["gpu_avg"] = gpuN > 0 ? (object)(int)Math.Round(gpuSum / gpuN) : null,
                ["fan_mode"] = cfg.LastMode,
                ["cpu_profile"] = cfg.Cpu.Enabled ? cfg.Cpu.Active : null,
            };
            if (fps != null) foreach (var kv in fps) r[kv.Key] = kv.Value;
            GameReports.Add(r);
            Log.Info($"Bilan de partie : {g.Name}, {secs / 60} min, {(fps != null ? fps["fps_avg"] + " FPS" : "FPS non mesurés")}");
        }
        Log.Info($"Jeu fermé : {g.Name}, retour aux réglages d'avant");
        try
        {
            if (g.WinKey && cfg.WinKeyLock != beforeWinKey) Tweaks.SetWinKey(cfg, beforeWinKey);
            if (!string.IsNullOrEmpty(g.Color) && !string.Equals(beforeColor, cfg.ActiveColor, StringComparison.OrdinalIgnoreCase))
                Tweaks.SetColor(cfg, string.IsNullOrEmpty(beforeColor) ? ScreenColor.NormalName : beforeColor);
            if (g.Gpu.HasValue && cfg.GpuLimit.On != beforeGpu) Tweaks.SetGpu(cfg, beforeGpu);
            if (!string.IsNullOrEmpty(g.Mode) && ModeDefs.Known(beforeMode) && cfg.LastMode != beforeMode) modes.Apply(beforeMode);
            if (ModeDefs.Known(g.CpuProfile) && ModeDefs.Known(beforeCpu) && cfg.Cpu.Active != beforeCpu) cpu.SetProfile(beforeCpu);
        }
        catch (Exception e)
        {
            Log.Error("Retour aux réglages d'avant le jeu", e);
        }
    }

    /// <summary>Programmes ouverts avec une fenêtre (pour choisir un jeu à ajouter).</summary>
    public static List<Dictionary<string, object>> RunningApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost", "PCMonitor",
            "StartMenuExperienceHost", "SearchHost", "LockApp", "Taskmgr", "CalculatorApp",
        };
        var list = new List<Dictionary<string, object>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || skip.Contains(p.ProcessName) || !seen.Add(p.ProcessName)) continue;
                var title = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new Dictionary<string, object> { ["exe"] = p.ProcessName + ".exe", ["title"] = title.Length > 60 ? title[..60] : title });
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderBy(d => (string)d["title"], StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
