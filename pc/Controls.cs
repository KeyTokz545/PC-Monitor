using System;
using System.Collections.Generic;
using System.Linq;

namespace PCMonitor;

/// <summary>État des réglages matériels (son, écran, clavier, batterie, GPU, ventilateurs) pour le téléphone.</summary>
public sealed class Controls
{
    readonly AppConfig cfg;
    readonly Asus asus;
    readonly ModeService modes;
    readonly GameWatcher games;
    bool? batterySupported;
    bool? overdriveSupported;

    public Controls(AppConfig cfg, Asus asus, ModeService modes, GameWatcher games)
    {
        this.cfg = cfg;
        this.asus = asus;
        this.modes = modes;
        this.games = games;
    }

    public bool BatterySupported => batterySupported ??= asus.Connected && asus.IsSupported(Asus.BatteryLimit);
    public bool OverdriveSupported => overdriveSupported ??= asus.Connected && asus.OverdriveSupported;

    static Dictionary<string, object> Kb(KeyboardSettings k) => new()
    {
        ["brightness"] = k.Brightness,
        ["mode"] = k.Mode,
        ["color"] = k.Color,
        ["color2"] = k.Color2,
        ["speed"] = k.Speed,
        ["keys"] = k.Keys,
        ["bar"] = k.Bar,
        ["logo"] = k.Logo,
        ["zones"] = k.Zones,
        ["method"] = k.Method,
        ["device"] = Keyboard.DeviceId,
        ["old"] = Keyboard.IsOld(),
    };

    public Dictionary<string, object> State()
    {
        var d = new Dictionary<string, object>();
        d["audio"] = Audio.State();

        var (refresh, list) = Display.Refresh();
        d["screen"] = new Dictionary<string, object>
        {
            ["brightness"] = Display.GetBrightness(),
            ["refresh"] = refresh,
            ["refresh_list"] = list,
            ["overdrive"] = OverdriveSupported ? (object)(asus.Get(Asus.ScreenOverdrive) == 1) : null,
        };

        d["keyboard"] = Keyboard.Supported ? Kb(cfg.Keyboard ?? new KeyboardSettings()) : null;

        d["battery"] = new Dictionary<string, object>
        {
            ["supported"] = BatterySupported,
            ["limit"] = cfg.BatteryLimit,
        };

        d["gpu"] = new Dictionary<string, object>
        {
            ["supported"] = Gpu.Supported,
            ["on"] = cfg.GpuLimit.On,
            ["mhz"] = cfg.GpuLimit.Mhz,
            ["fixed"] = cfg.GpuLimit.Fixed,
            ["max"] = Gpu.MaxClock(),
            ["tune_supported"] = Gpu.Supported && Gpu.TuneSupported,
            ["tune_on"] = cfg.GpuLimit.TuneOn,
            ["core"] = cfg.GpuLimit.CoreOffset,
            ["mem"] = cfg.GpuLimit.MemOffset,
            ["tune_err"] = Gpu.Supported && !Gpu.TuneSupported ? Gpu.NvError : null,
        };

        var (tpFound, tpOn) = Touchpad.State();
        d["touchpad"] = new Dictionary<string, object>
        {
            ["supported"] = tpFound,
            ["on"] = tpOn,
        };

        var fanModes = new Dictionary<string, object>();
        foreach (var m in ModeDefs.Order)
        {
            var (dc, dg) = modes.DefaultCurves(m);
            cfg.FanCurves.TryGetValue(m, out var p);
            fanModes[m] = new Dictionary<string, object>
            {
                ["label"] = cfg.ModeLabel(m),
                ["enabled"] = p?.Enabled ?? false,
                ["cpu"] = p != null && p.Cpu.Count == 16 ? p.Cpu : dc,
                ["gpu"] = p != null && p.Gpu.Count == 16 ? p.Gpu : dg,
                ["default_cpu"] = dc,
                ["default_gpu"] = dg,
            };
        }
        d["fans"] = new Dictionary<string, object>
        {
            ["supported"] = asus.Connected,
            ["current"] = string.IsNullOrEmpty(cfg.LastMode) ? null : cfg.LastMode,
            ["modes"] = fanModes,
        };

        lock (AppConfig.Gate)
        {
            d["color"] = new Dictionary<string, object>
            {
                ["active"] = cfg.ActiveColor,
                ["profiles"] = cfg.ColorProfiles.Select(c => new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["gamma"] = c.Gamma,
                    ["contrast"] = c.Contrast,
                    ["brightness"] = c.Brightness,
                    ["temperature"] = c.Temperature,
                    ["saturation"] = c.Saturation,
                }).ToList(),
                ["error"] = ScreenColor.LastError,
            };
            d["games"] = new Dictionary<string, object>
            {
                ["profiles"] = cfg.Games.Select(g => new Dictionary<string, object>
                {
                    ["exe"] = g.Exe,
                    ["name"] = g.Name,
                    ["enabled"] = g.Enabled,
                    ["mode"] = g.Mode,
                    ["gpu"] = g.Gpu,
                    ["color"] = g.Color,
                    ["winkey"] = g.WinKey,
                    ["cpu"] = g.CpuProfile,
                    ["gpu_core"] = g.GpuCore,
                    ["gpu_mem"] = g.GpuMem,
                }).ToList(),
                ["active"] = games.ActiveName,
                ["active_exe"] = games.ActiveExe,
                ["since"] = games.Since.HasValue ? (object)(long)(DateTime.Now - games.Since.Value).TotalSeconds : null,
            };
        }
        d["winkey"] = WinKey.Enabled;
        d["osd"] = new Dictionary<string, object>
        {
            ["on"] = cfg.Osd.On,
            ["corner"] = cfg.Osd.Corner,
            ["accent"] = cfg.Osd.Accent,
        };
        d["update"] = new Dictionary<string, object>
        {
            ["current"] = Program.Version,
            ["state"] = Updater.State,
            ["latest"] = Updater.Latest,
            ["error"] = Updater.Error,
            ["progress"] = Updater.Progress,
            ["checked"] = Updater.CheckedAt.HasValue ? (object)(long)(DateTime.Now - Updater.CheckedAt.Value).TotalSeconds : null,
            ["has_token"] = Updater.HasToken,
        };
        d["keys"] = new Dictionary<string, object> { ["pause"] = cfg.Keys.Pause, ["print"] = cfg.Keys.Print };
        List<string> close;
        lock (AppConfig.Gate) close = cfg.PreGame.Close.ToList();
        d["pregame"] = new Dictionary<string, object>
        {
            ["close"] = close,
            ["force"] = cfg.PreGame.Force,
            ["ram"] = cfg.PreGame.Ram,
            ["mode"] = cfg.PreGame.Mode,
            ["cpu"] = cfg.PreGame.Cpu,
        };
        d["volumes"] = Volumes();
        d["timer"] = Tweaks.TimerAt.HasValue
            ? new Dictionary<string, object> { ["what"] = Tweaks.TimerWhat, ["left"] = (long)Math.Max(0, (Tweaks.TimerAt.Value - DateTime.Now).TotalSeconds) }
            : null;
        return d;
    }

    /// <summary>Lecteurs du PC (C:, D:…) : place totale et libre.</summary>
    static List<Dictionary<string, object>> Volumes()
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            foreach (var v in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (v.DriveType != System.IO.DriveType.Fixed || !v.IsReady) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        ["name"] = v.Name.TrimEnd('\\'),
                        ["label"] = v.VolumeLabel,
                        ["total"] = Math.Round(v.TotalSize / 1073741824.0, 1),
                        ["free"] = Math.Round(v.AvailableFreeSpace / 1073741824.0, 1),
                    });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Remet les réglages que le PC oublie au redémarrage (GPU, clavier, batterie).</summary>
    public void Reapply()
    {
        try
        {
            Tweaks.ReapplyGpu(cfg);
        }
        catch (Exception e) { Log.Error("Réglage GPU au démarrage", e); }
        try
        {
            if (cfg.Keyboard != null) Keyboard.Apply(cfg.Keyboard);
        }
        catch (Exception e) { Log.Error("Éclairage au démarrage", e); }
        try
        {
            if (cfg.BatteryLimit >= 40 && cfg.BatteryLimit <= 100 && BatterySupported) asus.Set(Asus.BatteryLimit, cfg.BatteryLimit);
        }
        catch (Exception e) { Log.Error("Limite de charge au démarrage", e); }
        try
        {
            if (!string.Equals(cfg.ActiveColor, ScreenColor.NormalName, StringComparison.OrdinalIgnoreCase)) Tweaks.SetColor(cfg, cfg.ActiveColor);
        }
        catch (Exception e) { Log.Error("Couleurs au démarrage", e); }
        try
        {
            if (cfg.WinKeyLock && !WinKey.Enabled) WinKey.Set(true);
        }
        catch (Exception e) { Log.Error("Touche Windows au démarrage", e); }
    }
}
