using System;
using Microsoft.Win32;

namespace PCMonitor;

/// <summary>
/// Captures de la Xbox Game Bar (Paramètres > Jeux > Captures). Quand NVIDIA s'occupe des captures, les couper évite
/// que Windows prépare ou filme quoi que ce soit en arrière-plan pendant les jeux. Réglage de l'utilisateur, réversible.
/// </summary>
public static class GameDvr
{
    const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    const string StoreKey = @"System\GameConfigStore";

    /// <summary>true = captures Game Bar actives, false = coupées, null = illisible.</summary>
    public static bool? State()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(DvrKey);
            using var s = Registry.CurrentUser.OpenSubKey(StoreKey);
            // absent = valeur par défaut de Windows (actif)
            bool app = (k?.GetValue("AppCaptureEnabled") as int? ?? 1) != 0;
            bool dvr = (s?.GetValue("GameDVR_Enabled") as int? ?? 1) != 0;
            return app && dvr;
        }
        catch
        {
            return null;
        }
    }

    public static (bool Ok, string Message) Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(DvrKey))
            {
                k.SetValue("AppCaptureEnabled", on ? 1 : 0, RegistryValueKind.DWord);
                if (!on) k.SetValue("HistoricalCaptureEnabled", 0, RegistryValueKind.DWord); // « Enregistrer ce qui s'est passé »
            }
            using (var s = Registry.CurrentUser.CreateSubKey(StoreKey))
                s.SetValue("GameDVR_Enabled", on ? 1 : 0, RegistryValueKind.DWord);
            Log.Info("Captures Game Bar : " + (on ? "actives" : "coupées"));
            return (true, on ? "Captures de la Game Bar réactivées" : "Captures de la Game Bar coupées : plus rien ne filme en arrière-plan côté Windows");
        }
        catch (Exception e)
        {
            Log.Error("Captures Game Bar", e);
            return (false, "Windows a refusé le changement");
        }
    }
}
