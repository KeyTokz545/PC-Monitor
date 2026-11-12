using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PCMonitor;

/// <summary>Change le mode Silencieux / Équilibré / Turbo (comme G-Helper, sans G-Helper).</summary>
public sealed class ModeService
{
    readonly AppConfig cfg;
    readonly Asus asus;
    readonly object gate = new object();

    public event Action<string> Changed;

    public ModeService(AppConfig cfg, Asus asus)
    {
        this.cfg = cfg;
        this.asus = asus;
    }

    static readonly byte[] FullFans = { 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100, 100, 100, 100, 100 };

    /// <summary>Applique un mode. Renvoie (réussi, message pour le téléphone).</summary>
    public (bool Ok, string Message) Apply(string mode, bool save = true)
    {
        if (!ModeDefs.Known(mode)) return (false, "Mode inconnu");
        lock (gate)
        {
            var label = cfg.ModeLabel(mode);
            string extra = "";
            bool ok;

            if (asus.Connected)
            {
                ok = asus.SetPerformanceMode(ModeDefs.AsusValue(mode)) == 1;
                Log.Info($"Mode {mode} : {(ok ? "OK" : "refusé par le BIOS")}");

                if (ok && cfg.FanCurves.TryGetValue(mode, out var profile) && profile.Enabled)
                {
                    var cpu = FanProfile.ToBytes(profile.Cpu);
                    var gpu = FanProfile.ToBytes(profile.Gpu);
                    if (Asus.IsValidCurve(cpu) && Asus.IsValidCurve(gpu))
                    {
                        Thread.Sleep(150);
                        int c = asus.SetFanCurve(false, cpu), g = asus.SetFanCurve(true, gpu);
                        if (c != 1 || g != 1)
                        {
                            c = asus.SetFanRange(false, cpu);
                            g = asus.SetFanRange(true, gpu);
                            if (c != 1 || g != 1)
                            {
                                asus.SetPerformanceMode(ModeDefs.AsusValue(mode)); // remet la courbe d'origine
                                extra = " (courbe des ventilos refusée par ce PC)";
                            }
                        }
                        Log.Info($"Courbe ventilos {mode} : CPU={c} GPU={g}");
                    }
                }
            }
            else if (Win.IsRunning("GHelper.exe"))
            {
                // Secours : le pilote ASUS n'est pas accessible, on passe par les raccourcis de G-Helper
                Win.Press(Win.VK_CONTROL, Win.VK_SHIFT, Win.VK_MENU, ModeDefs.GHelperKey(mode));
                ok = true;
                extra = " (via G-Helper)";
            }
            else
            {
                return (false, "Contrôle ASUS indisponible : relance Keystone en administrateur");
            }

            if (ok)
            {
                if (!cfg.Cpu.Enabled && cfg.CpuBoost.TryGetValue(mode, out var boost) && boost >= 0 && boost <= 4)
                {
                    try { Win.SetCpuBoost(boost); } catch (Exception e) { Log.Error("Turbo Boost", e); }
                }
                cfg.LastMode = mode;
                if (save) cfg.Save();
                try { Changed?.Invoke(mode); } catch { }
            }
            return (ok, ok ? $"Mode {label}{extra}" : $"Le PC a refusé le mode {label}");
        }
    }

    static readonly Dictionary<string, int[]> Presets = new()
    {
        ["silent"] = new[] { 0, 0, 10, 20, 30, 45, 60, 80 },
        ["balanced"] = new[] { 0, 10, 20, 30, 45, 60, 75, 90 },
        ["turbo"] = new[] { 20, 30, 40, 55, 70, 85, 95, 100 },
    };

    readonly Dictionary<string, (List<int> Cpu, List<int> Gpu)> defaults = new();

    /// <summary>Courbes d'usine du mode (lues dans le BIOS, sinon valeurs typiques).</summary>
    public (List<int> Cpu, List<int> Gpu) DefaultCurves(string mode)
    {
        lock (defaults)
        {
            if (defaults.TryGetValue(mode, out var d)) return d;
            List<int> Read(bool gpu)
            {
                var c = asus.Connected ? asus.GetFanCurve(gpu, ModeDefs.AsusValue(mode)) : null;
                if (c != null) return c.Select(x => (int)x).ToList();
                var speeds = Presets.TryGetValue(mode, out var p) ? p : Presets["balanced"];
                return FanProfile.Temps.Concat(speeds).ToList();
            }
            d = (Read(false), Read(true));
            defaults[mode] = d;
            return d;
        }
    }

    /// <summary>Réapplique le dernier mode (au démarrage et au réveil : le BIOS l'oublie).</summary>
    public void Reapply()
    {
        if (string.IsNullOrEmpty(cfg.LastMode)) return;
        try { Apply(cfg.LastMode, save: false); }
        catch (Exception e) { Log.Error("Réapplication du mode", e); }
    }
}
