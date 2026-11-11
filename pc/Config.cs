using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMonitor;

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🧩";
    public string Open { get; set; } = "";
    public string Close { get; set; }
}

/// <summary>Courbe de ventilateurs d'un mode : 8 températures (°C) puis 8 vitesses (%), pour le CPU et le GPU.</summary>
public sealed class FanProfile
{
    public bool Enabled { get; set; }
    public List<int> Cpu { get; set; } = new();
    public List<int> Gpu { get; set; } = new();

    public static readonly int[] Temps = { 30, 40, 50, 60, 70, 80, 90, 100 };

    public static FanProfile Full() => new()
    {
        Enabled = true,
        Cpu = Temps.Concat(Enumerable.Repeat(100, 8)).ToList(),
        Gpu = Temps.Concat(Enumerable.Repeat(100, 8)).ToList(),
    };

    public bool IsFull => Enabled && Cpu.Count == 16 && Gpu.Count == 16 && Cpu.Skip(8).All(v => v >= 100) && Gpu.Skip(8).All(v => v >= 100);

    public static byte[] ToBytes(List<int> curve) =>
        curve != null && curve.Count == 16 ? curve.Select((v, i) => (byte)Math.Clamp(v, 0, i < 8 ? 120 : 100)).ToArray() : null;
}

public sealed class GpuLimitSettings
{
    public bool On { get; set; }
    public int Mhz { get; set; } = 1755;
}

/// <summary>Réglages, enregistrés dans %AppData%\PCMonitor\config.json.</summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 8765;
    public int DiscoveryPort { get; set; } = 8766;
    public string Token { get; set; } = "";
    public string LatencyHost { get; set; } = "1.1.1.1";
    public int LatencyPort { get; set; } = 443;
    public List<string> Modes { get; set; } = new() { "silent", "balanced", "turbo" };
    public Dictionary<string, string> ModeNames { get; set; } = new();
    public string LastMode { get; set; } = "";
    /// <summary>Ancien réglage « ventilos à fond » (converti en courbe au chargement).</summary>
    public Dictionary<string, bool> FansMax { get; set; } = new() { ["turbo"] = true };
    /// <summary>Courbes de ventilateurs personnalisées, par mode.</summary>
    public Dictionary<string, FanProfile> FanCurves { get; set; } = new();
    public GpuLimitSettings GpuLimit { get; set; } = new();
    /// <summary>Éclairage du clavier (null tant qu'il n'a jamais été réglé depuis PC Monitor).</summary>
    public KeyboardSettings Keyboard { get; set; }
    /// <summary>Limite de charge de la batterie en % (0 = ne pas y toucher).</summary>
    public int BatteryLimit { get; set; }
    /// <summary>Turbo Boost Windows par mode : 0 désactivé, 1 activé, 2 agressif.</summary>
    public Dictionary<string, int> CpuBoost { get; set; } = new() { ["silent"] = 1, ["balanced"] = 2, ["turbo"] = 2 };
    /// <summary>Programmes lancés en même temps que PC Monitor (ThrottleStop, Afterburner…).</summary>
    public List<string> LaunchWith { get; set; } = new();
    /// <summary>Profils de couleur de l'écran (le premier est toujours « Normal »).</summary>
    public List<ColorProfile> ColorProfiles { get; set; }
    public string ActiveColor { get; set; } = ScreenColor.NormalName;
    /// <summary>Profils de jeu appliqués automatiquement au lancement du jeu.</summary>
    public List<GameProfile> Games { get; set; } = new();
    public bool WinKeyLock { get; set; }
    /// <summary>Réglages du processeur (remplace ThrottleStop).</summary>
    public CpuSettings Cpu { get; set; } = new();
    public bool FirstRunDone { get; set; }
    public List<AppEntry> Apps { get; set; } = DefaultApps();

    static List<AppEntry> DefaultApps() => new()
    {
        new AppEntry { Name = "Steam", Icon = "🎮", Open = "steam://open/main", Close = "steam.exe" },
        new AppEntry { Name = "Discord", Icon = "💬", Open = @"%LOCALAPPDATA%\Discord\Update.exe --processStart Discord.exe", Close = "Discord.exe" },
        new AppEntry { Name = "Spotify", Icon = "🎵", Open = "spotify:", Close = "Spotify.exe" },
        new AppEntry { Name = "YouTube", Icon = "📺", Open = "https://www.youtube.com" },
    };

    [JsonIgnore] public static readonly object Gate = new object();

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string FilePath => Path.Combine(Log.Dir, "config.json");

    public string ModeLabel(string id) =>
        ModeNames != null && ModeNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : ModeDefs.Label(id);

    public static AppConfig Load()
    {
        AppConfig cfg = null;
        try
        {
            if (File.Exists(FilePath))
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opts);
            }
            else
            {
                // Première fois : on reprend les réglages de l'ancienne version Python
                // si config.json est posé à côté de PCMonitor.exe (même clé, mêmes applis).
                var old = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (File.Exists(old))
                {
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(old), Opts);
                    Log.Info("Réglages importés depuis " + old);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Lecture de config.json", e);
            try { File.Copy(FilePath, FilePath + ".abime", true); } catch { }
        }

        cfg ??= new AppConfig();
        cfg.Modes = (cfg.Modes ?? new()).Where(ModeDefs.Known).Distinct().ToList();
        if (cfg.Modes.Count == 0) cfg.Modes = new() { "silent", "balanced", "turbo" };
        cfg.ModeNames ??= new();
        cfg.FansMax ??= new();
        cfg.FanCurves ??= new();
        cfg.GpuLimit ??= new GpuLimitSettings();
        // « Ventilos à fond » de la version 2.0 : devient une courbe à 100 %
        foreach (var kv in cfg.FansMax.Where(kv => kv.Value).ToList())
            if (!cfg.FanCurves.ContainsKey(kv.Key))
                cfg.FanCurves[kv.Key] = FanProfile.Full();
        cfg.FansMax.Clear();
        cfg.CpuBoost ??= new();
        if (cfg.ColorProfiles == null || cfg.ColorProfiles.Count == 0) cfg.ColorProfiles = ColorProfile.Defaults();
        cfg.ColorProfiles = cfg.ColorProfiles.Where(c => c != null).Select(c => c.Clamp())
            .Where(c => !string.Equals(c.Name, ScreenColor.NormalName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        cfg.ColorProfiles.Insert(0, new ColorProfile());
        if (!cfg.ColorProfiles.Any(c => string.Equals(c.Name, cfg.ActiveColor, StringComparison.OrdinalIgnoreCase)))
            cfg.ActiveColor = ScreenColor.NormalName;
        cfg.Cpu ??= new();
        cfg.Cpu.Modes ??= new();
        foreach (var t in cfg.Cpu.Modes.Values) t?.Clamp();
        cfg.Cpu.CoreMv = Math.Clamp(cfg.Cpu.CoreMv, -250, 0);
        cfg.Cpu.GpuMv = Math.Clamp(cfg.Cpu.GpuMv, -250, 0);
        cfg.Cpu.SaMv = Math.Clamp(cfg.Cpu.SaMv, -250, 0);
        cfg.Games = (cfg.Games ?? new()).Where(g => g != null && !string.IsNullOrWhiteSpace(g.Exe)).ToList();
        cfg.LaunchWith ??= new();
        cfg.Apps ??= new();
        cfg.Apps = cfg.Apps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Open)).ToList();
        if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = NewToken();
        cfg.Save();
        return cfg;
    }

    public static string NewToken() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e)
            {
                Log.Error("Écriture de config.json", e);
            }
        }
    }
}

public static class ModeDefs
{
    public static readonly string[] Order = { "silent", "balanced", "turbo" };

    public static bool Known(string id) => Array.IndexOf(Order, id) >= 0;

    public static string Label(string id) => id switch
    {
        "silent" => "Silencieux",
        "balanced" => "Équilibré",
        "turbo" => "Turbo",
        _ => id,
    };

    /// <summary>Valeur ASUS du mode : 0 équilibré, 1 turbo, 2 silencieux.</summary>
    public static int AsusValue(string id) => id switch
    {
        "silent" => 2,
        "turbo" => 1,
        _ => 0,
    };

    /// <summary>Touche F16/F17/F18 utilisée par G-Helper (solution de secours).</summary>
    public static byte GHelperKey(string id) => id switch
    {
        "silent" => 0x7F,
        "turbo" => 0x81,
        _ => 0x80,
    };
}
