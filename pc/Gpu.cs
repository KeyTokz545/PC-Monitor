using System;
using System.Globalization;

namespace PCMonitor;

/// <summary>Bridage de la fréquence de la carte graphique NVIDIA (comme « Core Clock Limit » de G-Helper).</summary>
public static class Gpu
{
    static bool? supported;
    static int maxClock = -1;

    public static bool Supported => supported ??= Detect();

    static bool Detect()
    {
        var (code, output) = Win.Run("nvidia-smi", "--query-gpu=name --format=csv,noheader", 8000);
        var ok = code == 0 && Win.Decode(output).Trim().Length > 0;
        Log.Info("nvidia-smi : " + (ok ? Win.Decode(output).Trim() : "indisponible"));
        return ok;
    }

    /// <summary>Fréquence maximale d'usine du GPU (MHz), pour borner le réglage.</summary>
    public static int MaxClock()
    {
        if (maxClock > 0 || !Supported) return maxClock;
        var (code, output) = Win.Run("nvidia-smi", "--query-gpu=clocks.max.graphics --format=csv,noheader,nounits", 8000);
        if (code == 0 && int.TryParse(Win.Decode(output).Trim().Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 300)
            maxClock = v;
        return maxClock;
    }

    /// <summary>Bride la fréquence (MHz) ou rend la main au pilote (mhz &lt;= 0). Renvoie (réussi, message).</summary>
    public static (bool Ok, string Message) SetLimit(int mhz)
    {
        if (!Supported) return (false, "Carte graphique NVIDIA introuvable (nvidia-smi)");
        var args = mhz > 0 ? $"-lgc 0,{mhz}" : "-rgc";
        var (code, output) = Win.Run("nvidia-smi", args, 15000);
        var text = Win.Decode(output).Trim();
        Log.Info($"nvidia-smi {args} : {code} {text}");
        if (code != 0)
            return (false, text.Length > 0 && text.Length < 160 ? "NVIDIA : " + text : "Le pilote NVIDIA a refusé le réglage");
        return (true, mhz > 0 ? $"GPU limité à {mhz} MHz" : "GPU en fréquence normale");
    }
}
