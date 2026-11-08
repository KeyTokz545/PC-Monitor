// Profils de couleur de l'écran : gamma, contraste, luminosité et température par la table gamma de la carte
// graphique (aucun impact sur les performances), saturation par le filtre couleur de Windows (Magnification).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PCMonitor;

public sealed class ColorProfile
{
    public string Name { get; set; } = "Normal";
    /// <summary>0,5 (plus sombre) à 2,0 (ombres éclaircies). 1 = neutre.</summary>
    public double Gamma { get; set; } = 1.0;
    /// <summary>50 à 150 %.</summary>
    public int Contrast { get; set; } = 100;
    /// <summary>-50 à +50.</summary>
    public int Brightness { get; set; }
    /// <summary>Température de couleur en kelvins, 3000 (chaud) à 9000 (froid). 6500 = neutre.</summary>
    public int Temperature { get; set; } = 6500;
    /// <summary>0 (noir et blanc) à 200 %.</summary>
    public int Saturation { get; set; } = 100;

    [JsonIgnore] public bool RampNeutral => Math.Abs(Gamma - 1) < 0.005 && Contrast == 100 && Brightness == 0 && Temperature == 6500;
    [JsonIgnore] public bool Neutral => RampNeutral && Saturation == 100;

    public ColorProfile Clamp()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Perso" : Name.Trim();
        if (Name.Length > 24) Name = Name[..24];
        Gamma = Math.Round(Math.Clamp(double.IsFinite(Gamma) ? Gamma : 1, 0.5, 2.0), 2);
        Contrast = Math.Clamp(Contrast, 50, 150);
        Brightness = Math.Clamp(Brightness, -50, 50);
        Temperature = Math.Clamp(Temperature, 3000, 9000);
        Saturation = Math.Clamp(Saturation, 0, 200);
        return this;
    }

    public ColorProfile Copy() => (ColorProfile)MemberwiseClone();

    public static List<ColorProfile> Defaults() => new()
    {
        new ColorProfile { Name = "Normal" },
        new ColorProfile { Name = "Couleurs vives", Saturation = 135, Contrast = 104 },
        new ColorProfile { Name = "Voir dans le noir", Gamma = 1.45, Brightness = 3, Contrast = 96, Saturation = 110 },
        new ColorProfile { Name = "Soirée", Temperature = 4300, Brightness = -4 },
    };
}

public static class ScreenColor
{
    public const string NormalName = "Normal";

    /// <summary>Exécute une fonction sur le fil de l'interface (le filtre Windows y est attaché). Réglé par TrayApp.</summary>
    public static Func<Func<bool>, bool> OnUi = f => f();

    static readonly object Gate = new();
    static ColorProfile current = new();
    static bool magReady, magActive, rangeUnlocked;

    public static ColorProfile Current => current;
    public static string LastError { get; private set; }

    // ---------------------------------------------------------------- table gamma (GDI)
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

    // ---------------------------------------------------------------- filtre couleur (Magnification)
    [DllImport("Magnification.dll")]
    static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    static extern bool MagSetFullscreenColorEffect([In] float[] effect);

    /// <summary>Gain de chaque canal pour une température (approximation de Tanner Helland), 6500 K = blanc neutre.</summary>
    static (double R, double G, double B) Kelvin(int k)
    {
        static (double, double, double) Raw(double kelvin)
        {
            double t = kelvin / 100.0, r, g, b;
            if (t <= 66)
            {
                r = 255;
                g = 99.4708025861 * Math.Log(t) - 161.1195681661;
                b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            }
            else
            {
                r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
                g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
                b = 255;
            }
            return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        }
        var (r0, g0, b0) = Raw(6500);
        var (r1, g1, b1) = Raw(k);
        double R = r1 / r0, G = g1 / g0, B = b1 / b0, max = Math.Max(R, Math.Max(G, B));
        return (R / max, G / max, B / max);
    }

    static ushort[] Ramp(ColorProfile p)
    {
        var ramp = new ushort[768];
        var (gr, gg, gb) = Kelvin(p.Temperature);
        double[] gains = { gr, gg, gb };
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            x = Math.Pow(x, 1.0 / p.Gamma);
            x = (x - 0.5) * (p.Contrast / 100.0) + 0.5;
            x += p.Brightness / 100.0;
            x = Math.Clamp(x, 0, 1);
            for (int c = 0; c < 3; c++)
                ramp[c * 256 + i] = (ushort)Math.Round(Math.Clamp(x * gains[c], 0, 1) * 65535);
        }
        return ramp;
    }

    /// <summary>Windows refuse les tables gamma trop éloignées du neutre, sauf si on élargit la plage (effet au prochain démarrage).</summary>
    static void UnlockRange()
    {
        if (rangeUnlocked) return;
        rangeUnlocked = true;
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
            if (key != null && !(key.GetValue("GdiICMGammaRange") is int v && v >= 256))
            {
                key.SetValue("GdiICMGammaRange", 256, RegistryValueKind.DWord);
                Log.Info("Plage gamma élargie (GdiICMGammaRange = 256)");
            }
        }
        catch (Exception e)
        {
            Log.Error("Plage gamma", e);
        }
    }

    static bool ApplyRamp(ColorProfile p)
    {
        var ramp = Ramp(p);
        bool any = false, all = true;
        foreach (var s in Screen.AllScreens)
        {
            var dc = CreateDC(s.DeviceName, null, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) { all = false; continue; }
            try
            {
                if (SetDeviceGammaRamp(dc, ramp)) any = true;
                else all = false;
            }
            finally
            {
                DeleteDC(dc);
            }
        }
        return any && all;
    }

    static float[] SaturationMatrix(double s)
    {
        const double lr = 0.2126, lg = 0.7152, lb = 0.0722;
        double k = 1 - s;
        return new[]
        {
            (float)(lr * k + s), (float)(lr * k), (float)(lr * k), 0f, 0f,
            (float)(lg * k), (float)(lg * k + s), (float)(lg * k), 0f, 0f,
            (float)(lb * k), (float)(lb * k), (float)(lb * k + s), 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f, 1f,
        };
    }

    static bool ApplySaturation(int percent)
    {
        return OnUi(() =>
        {
            try
            {
                if (percent == 100)
                {
                    if (magActive && magReady) MagSetFullscreenColorEffect(SaturationMatrix(1));
                    magActive = false;
                    return true;
                }
                if (!magReady) magReady = MagInitialize();
                if (!magReady) return false;
                magActive = MagSetFullscreenColorEffect(SaturationMatrix(percent / 100.0));
                return magActive;
            }
            catch (Exception e)
            {
                Log.Error("Filtre de saturation", e);
                return false;
            }
        });
    }

    /// <summary>Applique un profil à tous les écrans. Renvoie (réussi, message).</summary>
    public static (bool Ok, string Message) Apply(ColorProfile p)
    {
        p = p.Copy().Clamp();
        bool rampOk;
        lock (Gate)
        {
            if (!p.RampNeutral) UnlockRange();
            rampOk = ApplyRamp(p);
            current = p;
        }
        // le filtre de saturation vit sur le fil de l'interface : hors du verrou pour ne jamais bloquer
        var satOk = ApplySaturation(p.Saturation);
        string error = null;
        if (!rampOk && !p.RampNeutral)
            error = "Windows a refusé ce réglage (trop fort). Redémarre le PC une fois pour débloquer les réglages extrêmes.";
        else if (!rampOk)
            error = "Impossible de remettre les couleurs d'origine sur un des écrans.";
        else if (!satOk)
            error = "Le filtre de saturation de Windows n'a pas pu être activé.";
        LastError = error;
        Log.Info($"Couleurs « {p.Name} » : gamma {p.Gamma} contraste {p.Contrast} lum {p.Brightness} {p.Temperature} K sat {p.Saturation} → {(error ?? "OK")}");
        return (error == null, error ?? $"Couleurs : {p.Name}");
    }

    /// <summary>Réapplique le profil courant (Windows remet la table gamma à zéro après une veille ou un changement d'écran).</summary>
    public static void Reapply()
    {
        var p = current;
        if (p.Neutral) return;
        lock (Gate)
        {
            try { ApplyRamp(p); } catch (Exception e) { Log.Error("Réapplication des couleurs", e); }
        }
    }

    /// <summary>Remet l'écran d'origine (à la fermeture de PC Monitor).</summary>
    public static void Reset()
    {
        try
        {
            if (System.Threading.Monitor.TryEnter(Gate, 500))
            {
                try
                {
                    if (!current.RampNeutral) ApplyRamp(new ColorProfile());
                    current = new ColorProfile();
                }
                finally
                {
                    System.Threading.Monitor.Exit(Gate);
                }
            }
            if (magReady)
            {
                if (magActive) MagSetFullscreenColorEffect(SaturationMatrix(1));
                MagUninitialize();
                magReady = magActive = false;
            }
        }
        catch { }
    }
}
