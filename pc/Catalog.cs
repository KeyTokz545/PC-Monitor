using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace PCMonitor;

public sealed class CatalogItem
{
    public string Name { get; set; }
    public string Open { get; set; }
    public string Close { get; set; }
    public string Src { get; set; }
    /// <summary>Programme visé par le raccourci (.exe), si connu.</summary>
    public string Target { get; set; }
}

/// <summary>Jeux Steam et programmes du Bureau / menu Démarrer, pour les ajouter depuis le téléphone.</summary>
public static class Catalog
{
    static volatile List<CatalogItem> items = new();
    static volatile bool loading;

    public static List<CatalogItem> Items => items;
    public static bool Loading => loading;

    public static void Refresh()
    {
        if (loading) return;
        loading = true;
        // WScript.Shell (lecture des raccourcis) préfère un thread STA
        var t = new Thread(Build) { IsBackground = true, Name = "Catalogue" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static readonly Regex Noise = new(
        @"uninstall|d[ée]sinstall|readme|lisez|\bhelp\b|\baide\b|manual|manuel|documentation|licen[cs]e|release notes|notes de version|" +
        @"website|site web|\bsupport\b|changelog|what's new|\bfaq\b|administrative tools|outils d.administration|windows tools|" +
        @"outils windows|accessibilit|system tools|outils syst|\\startup\\|\\démarrage\\",
        RegexOptions.IgnoreCase);

    static readonly Regex SteamNoise = new(
        @"redistributable|steamworks|proton|steam linux runtime|soundtrack|dedicated server|\bsdk\b|benchmark", RegexOptions.IgnoreCase);

    static void Build()
    {
        try
        {
            var raw = new List<CatalogItem>();
            raw.AddRange(SteamGames());
            raw.AddRange(Shortcuts());
            var order = new Dictionary<string, int> { ["Steam"] = 0, ["Bureau"] = 1, ["Menu Démarrer"] = 2 };
            var seen = new HashSet<string>();
            var list = new List<CatalogItem>();
            foreach (var it in raw.OrderBy(x => order.GetValueOrDefault(x.Src, 3)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var key = Regex.Replace(it.Name.ToLowerInvariant(), "[^a-z0-9]", "");
                if (key.Length > 0 && seen.Add(key)) list.Add(it);
            }
            items = list.Take(500).ToList();
            Log.Info($"Catalogue : {items.Count} applis trouvées");
        }
        catch (Exception e)
        {
            Log.Error("Catalogue", e);
        }
        finally
        {
            loading = false;
        }
    }

    static IEnumerable<CatalogItem> SteamGames()
    {
        string steam = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steam = key?.GetValue("SteamPath") as string;
        }
        catch { }
        if (string.IsNullOrEmpty(steam) || !Directory.Exists(steam)) return Enumerable.Empty<CatalogItem>();

        var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steam) };
        try
        {
            var vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        catch { }

        var games = new List<CatalogItem>();
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var t = File.ReadAllText(acf);
                    var id = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"");
                    var name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"");
                    if (id.Success && name.Success && !SteamNoise.IsMatch(name.Groups[1].Value))
                        games.Add(new CatalogItem { Name = name.Groups[1].Value, Open = "steam://rungameid/" + id.Groups[1].Value, Src = "Steam" });
                }
                catch { }
            }
        }
        return games;
    }

    static IEnumerable<CatalogItem> Shortcuts()
    {
        var dirs = new (Environment.SpecialFolder Folder, string Src)[]
        {
            (Environment.SpecialFolder.DesktopDirectory, "Bureau"),
            (Environment.SpecialFolder.CommonDesktopDirectory, "Bureau"),
            (Environment.SpecialFolder.Programs, "Menu Démarrer"),
            (Environment.SpecialFolder.CommonPrograms, "Menu Démarrer"),
        };
        dynamic shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); } catch { }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var found = new List<CatalogItem>();
        foreach (var (folder, src) in dirs)
        {
            var dir = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*", options))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".url") continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (Noise.IsMatch(name) || Noise.IsMatch(path)) continue;
                var source = src;
                string target = null;
                try
                {
                    if (ext == ".lnk" && shell != null)
                    {
                        dynamic link = shell.CreateShortcut(path);
                        target = (string)link.TargetPath;
                    }
                    else if (ext == ".url" && File.ReadAllText(path).Contains("steam://"))
                    {
                        source = "Steam";
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(target) && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue; // raccourci vers un .pdf, un dossier…
                found.Add(new CatalogItem
                {
                    Name = name,
                    Open = path,
                    Close = string.IsNullOrEmpty(target) ? null : Path.GetFileName(target),
                    Src = source,
                    Target = string.IsNullOrEmpty(target) ? null : target,
                });
            }
        }
        return found;
    }

    static readonly (string Pattern, string Icon)[] IconRules =
    {
        (@"call of duty|warzone|\bcod\b|battlefield|counter-strike|valorant|apex|rainbow six|pubg", "🔫"),
        (@"battle\.net", "🎯"),
        (@"discord|teamspeak|whatsapp|messenger|telegram|teams|zoom|skype", "💬"),
        (@"spotify|deezer|music|musique|itunes|tidal", "🎵"),
        (@"chrome|edge|firefox|opera|brave|vivaldi", "🌐"),
        (@"\bobs\b|streamlabs|record|capture|shadowplay|nvidia app|geforce", "🎥"),
        (@"vlc|netflix|youtube|prime video|disney|plex|kodi|video|vidéo", "📺"),
        (@"beamng|forza|\bf1\b|racing|gran turismo|dirt|assetto|need for speed|trackmania|snowrunner", "🏎️"),
        (@"\bfc ?\d|fifa|nba|rocket league|football|\bpes\b", "⚽"),
        (@"afterburner|throttlestop|g-?helper|hwinfo|libre ?hardware|cpu-?z|gpu-?z|armoury", "🛠️"),
        (@"epic games|ubisoft|ea app|origin|gog|launcher|xbox", "🕹️"),
        (@"word|excel|powerpoint|office|libreoffice|notepad|bloc-notes|acrobat|pdf", "📄"),
        (@"explor|fichiers|files|winrar|7-zip", "📁"),
        (@"steam", "🎮"),
    };

    public static string GuessIcon(string name, string src)
    {
        foreach (var (pattern, icon) in IconRules)
            if (Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase)) return icon;
        return src == "Steam" ? "🎮" : "🧩";
    }

    /// <summary>ThrottleStop et MSI Afterburner trouvés sur le PC (pour « Lancer avec PC Monitor »).</summary>
    public static List<(string Name, string Path)> FindTools()
    {
        var found = new List<(string Name, string Path)>();
        var ab = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner", "MSIAfterburner.exe");
        if (File.Exists(ab)) found.Add(("MSI Afterburner", ab));
        foreach (var it in items)
        {
            if (it.Target == null || !Regex.IsMatch(it.Name, "throttlestop|afterburner", RegexOptions.IgnoreCase)) continue;
            if (!File.Exists(it.Target) || found.Any(f => string.Equals(f.Path, it.Target, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add((it.Name, it.Target));
        }
        return found;
    }
}
