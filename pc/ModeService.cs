using System;
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

                if (ok && cfg.FansMax.TryGetValue(mode, out var max) && max)
                {
                    Thread.Sleep(150);
                    int c = asus.SetFanCurve(false, FullFans), g = asus.SetFanCurve(true, FullFans);
                    if (c != 1 || g != 1)
                    {
                        c = asus.SetFanRange(false, FullFans);
                        g = asus.SetFanRange(true, FullFans);
                        if (c != 1 || g != 1)
                        {
                            asus.SetPerformanceMode(ModeDefs.AsusValue(mode)); // remet la courbe d'origine
                            extra = " (ventilos à fond non pris en charge par ce PC)";
                        }
                    }
                    Log.Info($"Ventilos à fond : CPU={c} GPU={g}");
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
                return (false, "Contrôle ASUS indisponible : relance PC Monitor en administrateur");
            }

            if (ok)
            {
                if (cfg.CpuBoost.TryGetValue(mode, out var boost) && boost >= 0 && boost <= 4)
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

    /// <summary>Réapplique le dernier mode (au démarrage et au réveil : le BIOS l'oublie).</summary>
    public void Reapply()
    {
        if (string.IsNullOrEmpty(cfg.LastMode)) return;
        try { Apply(cfg.LastMode, save: false); }
        catch (Exception e) { Log.Error("Réapplication du mode", e); }
    }
}
