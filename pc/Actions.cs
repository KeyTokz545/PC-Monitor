using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PCMonitor;

/// <summary>Actions demandées par le téléphone.</summary>
public sealed class Actions
{
    readonly AppConfig cfg;
    readonly ModeService modes;
    readonly Asus asus;
    readonly GameWatcher games;

    public CpuTuner Cpu { get; }

    /// <summary>Remise à zéro des températures max / min (fournie par TrayApp).</summary>
    public Action ResetPeaks { get; set; }

    public Actions(AppConfig cfg, ModeService modes, Asus asus, GameWatcher games, CpuTuner cpu)
    {
        this.cfg = cfg;
        this.modes = modes;
        this.asus = asus;
        this.games = games;
        Cpu = cpu;
    }

    static double D(JsonElement b, string name, double fallback) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : fallback;

    static bool IsNull(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Null;

    /// <summary>Liste de couleurs « #RRGGBB ».</summary>
    static List<string> Colors(JsonElement b, string name)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            var c = x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            if (c == null || !System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$")) return null;
            list.Add(c.ToUpperInvariant());
        }
        return list;
    }

    static bool? B(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    static bool Has(JsonElement b, string name) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out _);

    static List<int> L(JsonElement b, string name)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n)) list.Add(n);
            else return null;
        return list;
    }

    static string S(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int I(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : -1;

    static Dictionary<string, object> R(bool ok, string msg = null) =>
        new() { ["ok"] = ok, ["msg"] = msg };

    public Dictionary<string, object> Do(JsonElement body)
    {
        var act = S(body, "action");
        switch (act)
        {
            case "mode":
            {
                var id = S(body, "mode");
                var (ok, msg) = modes.Apply(id);
                if (ok) Osd.Mode(id);
                return R(ok, msg);
            }
            // ---------------------------------------------------------------- mise à jour
            case "update_check":
                Updater.Check().Wait(TimeSpan.FromSeconds(8));
                return R(true);
            case "update_install":
            {
                var (ok, msg) = Updater.Install();
                return R(ok, msg);
            }
            case "update_token":
            {
                Updater.SetToken(S(body, "token"));
                Updater.Check().Wait(TimeSpan.FromSeconds(8));
                return R(true, Updater.HasToken ? "Jeton enregistré" : "Jeton retiré");
            }

            // ---------------------------------------------------------------- touches dédiées
            case "keys_set":
            {
                var pause = S(body, "pause");
                var print = S(body, "print");
                if (pause != null && HotKeys.Choices.Contains(pause)) cfg.Keys.Pause = pause;
                if (print != null && HotKeys.Choices.Contains(print)) cfg.Keys.Print = print;
                cfg.Save();
                HotKeys.Apply();
                return R(true);
            }

            // ---------------------------------------------------------------- avant de jouer
            case "pregame_run":
            {
                var (ok, msg) = PreGame.Run(cfg, modes, Cpu);
                return R(ok, msg);
            }
            case "pregame_set":
            {
                var pg = cfg.PreGame;
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("close", out var cl) && cl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var x in cl.EnumerateArray())
                    {
                        var exe = x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "").Trim() : "";
                        if (GameProfile.Norm(exe).Length == 0) continue;
                        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                        if (!list.Any(e => GameProfile.Norm(e) == GameProfile.Norm(exe))) list.Add(Cut(exe, 80));
                    }
                    lock (AppConfig.Gate) pg.Close = list.Take(30).ToList();
                }
                if (B(body, "force") is bool force) pg.Force = force;
                if (B(body, "ram") is bool ram) pg.Ram = ram;
                if (Has(body, "mode")) pg.Mode = ModeDefs.Known(S(body, "mode")) ? S(body, "mode") : "";
                if (Has(body, "cpu")) pg.Cpu = ModeDefs.Known(S(body, "cpu")) ? S(body, "cpu") : "";
                cfg.Save();
                return R(true);
            }

            case "osd_set":
            {
                if (B(body, "on") is bool on) cfg.Osd.On = on;
                var corner = S(body, "corner");
                if (corner is "tl" or "tr" or "bl" or "br") cfg.Osd.Corner = corner;
                var accent = S(body, "accent");
                if (accent != null && System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9A-Fa-f]{6}$")) cfg.Osd.Accent = accent.ToUpperInvariant();
                cfg.Save();
                if (B(body, "test") == true || (Has(body, "corner") && cfg.Osd.On)) Osd.Test();
                return R(true, Has(body, "on") ? (cfg.Osd.On ? "Affichage sur le PC activé" : "Affichage sur le PC désactivé") : null);
            }
            case "vol_up": Win.Media(Win.VK_VOLUME_UP); return R(true);
            case "vol_down": Win.Media(Win.VK_VOLUME_DOWN); return R(true);
            case "vol_mute": Win.Media(Win.VK_VOLUME_MUTE); return R(true);
            case "play": Win.Media(Win.VK_MEDIA_PLAY); return R(true);
            case "next": Win.Media(Win.VK_MEDIA_NEXT); return R(true);
            case "prev": Win.Media(Win.VK_MEDIA_PREV); return R(true);

            case "app_open":
            {
                var app = AppAt(I(body, "index"));
                if (app == null) return R(false, "Appli introuvable");
                try { Win.OpenApp(app.Open); }
                catch (Exception e) { Log.Error("Ouverture " + app.Name, e); return R(false, $"Impossible d'ouvrir {app.Name}"); }
                return R(true, $"{app.Name} ouvert");
            }
            case "app_close":
            {
                var app = AppAt(I(body, "index"));
                if (app == null) return R(false, "Appli introuvable");
                if (string.IsNullOrEmpty(app.Close)) return R(false, "Pas de programme à fermer pour cette appli");
                Win.CloseApp(app.Close);
                return R(true, $"{app.Name} fermé");
            }
            case "app_add":
            {
                var list = Catalog.Items;
                int id = I(body, "id");
                if (id < 0 || id >= list.Count) return R(false, "Appli introuvable, actualise la liste");
                var it = list[id];
                lock (AppConfig.Gate)
                {
                    if (cfg.Apps.Any(a => a.Open == it.Open)) return R(false, $"{it.Name} est déjà dans tes applis");
                    var name = S(body, "name");
                    var icon = S(body, "icon");
                    cfg.Apps.Add(new AppEntry
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? it.Name : Cut(name, 40),
                        Icon = string.IsNullOrWhiteSpace(icon) ? Catalog.GuessIcon(it.Name, it.Src) : Cut(icon, 8),
                        Open = it.Open,
                        Close = it.Close,
                    });
                }
                cfg.Save();
                return R(true, $"{it.Name} ajouté");
            }
            case "app_update":
            {
                var app = AppAt(I(body, "index"));
                if (app == null) return R(false, "Appli introuvable");
                var icon = S(body, "icon");
                var name = S(body, "name");
                if (!string.IsNullOrWhiteSpace(icon)) app.Icon = Cut(icon, 8);
                if (!string.IsNullOrWhiteSpace(name)) app.Name = Cut(name, 40);
                cfg.Save();
                return R(true, "Icône changée");
            }
            case "app_remove":
            {
                AppEntry removed;
                lock (AppConfig.Gate)
                {
                    int i = I(body, "index");
                    if (i < 0 || i >= cfg.Apps.Count) return R(false, "Appli introuvable");
                    removed = cfg.Apps[i];
                    cfg.Apps.RemoveAt(i);
                }
                cfg.Save();
                return R(true, $"{removed.Name} retiré");
            }

            // ---------------------------------------------------------------- son
            case "volume_set":
                Audio.SetVolume(I(body, "value"));
                return R(true);
            case "mute_toggle":
            {
                var muted = Audio.ToggleMute();
                Osd.Toggle("Son", muted ? "Coupé" : "Rétabli", !muted, muted ? Osd.Red : Osd.Green);
                return R(true, muted ? "Son coupé" : "Son rétabli");
            }
            case "mic_toggle":
            {
                var muted = Audio.ToggleMic();
                if (asus.Connected) asus.Set(Asus.MicMuteLed, muted ? 1 : 0);
                Osd.Toggle("Micro", muted ? "Coupé" : "Activé", !muted, muted ? Osd.Red : Osd.Green);
                return R(true, muted ? "Micro coupé" : "Micro activé");
            }
            case "output_set":
            {
                var ok = Audio.SetOutput(S(body, "id"));
                return R(ok, ok ? "Sortie audio changée" : "Impossible de changer la sortie audio");
            }

            // ---------------------------------------------------------------- écran
            case "brightness_set":
                return R(Display.SetBrightness(I(body, "value")));
            case "refresh_set":
            {
                int hz = I(body, "value");
                var ok = hz > 0 && Display.SetRefresh(hz);
                return R(ok, ok ? $"Écran à {hz} Hz" : "Fréquence refusée par l'écran");
            }
            case "overdrive_set":
            {
                var on = B(body, "on") ?? false;
                var ok = asus.Connected && asus.Set(Asus.ScreenOverdrive, on ? 1 : 0) == 1;
                return R(ok, ok ? (on ? "Overdrive activé" : "Overdrive désactivé") : "Overdrive non pris en charge");
            }
            case "screen_off":
                Task.Run(async () => { await Task.Delay(600); Display.Off(); });
                return R(true, "Écran éteint");

            // ---------------------------------------------------------------- clavier
            case "kb_set":
            {
                KeyboardSettings k;
                lock (AppConfig.Gate)
                {
                    k = cfg.Keyboard ?? new KeyboardSettings();
                    if (Has(body, "brightness")) k.Brightness = I(body, "brightness");
                    if (Has(body, "mode")) k.Mode = I(body, "mode");
                    if (Has(body, "speed")) k.Speed = I(body, "speed");
                    if (S(body, "color") is string c1) k.Color = c1;
                    if (S(body, "color2") is string c2) k.Color2 = c2;
                    if (B(body, "keys") is bool keys) k.Keys = keys;
                    if (B(body, "bar") is bool bar) k.Bar = bar;
                    if (B(body, "logo") is bool logo) k.Logo = logo;
                    if (Has(body, "method")) k.Method = Math.Clamp(I(body, "method"), 1, 3);
                    if (IsNull(body, "zones")) k.Zones = null;
                    else if (Colors(body, "zones") is { Count: 4 } zones) k.Zones = zones;
                    cfg.Keyboard = k;
                }
                var (ok, msg) = Keyboard.Apply(k);
                cfg.Save();
                return R(ok, ok ? null : msg);
            }

            // ---------------------------------------------------------------- batterie
            case "battery_set":
            {
                int v = I(body, "value");
                if (v < 40 || v > 100) return R(false, "Valeur entre 40 et 100 %");
                var ok = asus.Connected && asus.Set(Asus.BatteryLimit, v) == 1;
                if (ok)
                {
                    cfg.BatteryLimit = v;
                    cfg.Save();
                }
                return R(ok, ok ? (v >= 100 ? "Charge complète autorisée" : $"Charge limitée à {v} %") : "Limite de charge non prise en charge");
            }

            // ---------------------------------------------------------------- GPU
            case "gpu_limit":
            {
                var on = B(body, "on") ?? cfg.GpuLimit.On;
                var (ok, msg) = Tweaks.SetGpu(cfg, on, I(body, "mhz"), B(body, "fixed"));
                return R(ok, msg);
            }
            case "gpu_tune":
            {
                var on = B(body, "on") ?? cfg.GpuLimit.TuneOn;
                var core = Has(body, "core") ? I(body, "core") : cfg.GpuLimit.CoreOffset;
                var mem = Has(body, "mem") ? I(body, "mem") : cfg.GpuLimit.MemOffset;
                var save = B(body, "save") ?? true;
                var (ok, msg) = Tweaks.SetGpuTune(cfg, on, core, mem, save);
                return R(ok, msg);
            }
            case "gpu_reset":
            {
                Tweaks.SetGpu(cfg, false);
                var (ok, msg) = Tweaks.SetGpuTune(cfg, false, 0, 0, true);
                return R(ok, ok ? "GPU remis à zéro (fréquence et décalage d'origine)" : msg);
            }

            // ---------------------------------------------------------------- ventilateurs
            case "fan_curve":
            {
                var mode = S(body, "mode");
                if (!ModeDefs.Known(mode)) return R(false, "Mode inconnu");
                var cpu = L(body, "cpu");
                var gpu = L(body, "gpu");
                var enabled = B(body, "enabled") ?? true;
                if (enabled && (!Asus.IsValidCurve(FanProfile.ToBytes(cpu)) || !Asus.IsValidCurve(FanProfile.ToBytes(gpu))))
                    return R(false, "Courbe invalide");
                lock (AppConfig.Gate)
                {
                    cfg.FanCurves.TryGetValue(mode, out var old);
                    cfg.FanCurves[mode] = new FanProfile
                    {
                        Enabled = enabled,
                        Cpu = cpu ?? old?.Cpu ?? new List<int>(),
                        Gpu = gpu ?? old?.Gpu ?? new List<int>(),
                    };
                }
                cfg.Save();
                var label = cfg.ModeLabel(mode);
                if (cfg.LastMode == mode)
                {
                    var (ok, msg) = modes.Apply(mode);
                    return R(ok, enabled ? $"Courbe {label} appliquée" + (msg.Contains('(') ? msg[msg.IndexOf('(')..].Insert(0, " ") : "") : $"Courbe d'usine en mode {label}");
                }
                return R(true, enabled ? $"Courbe {label} enregistrée (appliquée en mode {label})" : $"Courbe d'usine en mode {label}");
            }

            // ---------------------------------------------------------------- couleurs de l'écran
            case "color_apply":
            {
                var (ok, msg) = Tweaks.SetColor(cfg, S(body, "name"));
                return R(ok, msg);
            }
            case "color_save":
            {
                var name = (S(body, "name") ?? "").Trim();
                if (name.Length == 0) return R(false, "Donne un nom au profil");
                if (string.Equals(name, ScreenColor.NormalName, StringComparison.OrdinalIgnoreCase))
                    return R(false, "Le profil Normal ne se modifie pas : crée un nouveau profil");
                var p = new ColorProfile
                {
                    Name = name,
                    Gamma = D(body, "gamma", 1),
                    Contrast = (int)Math.Round(D(body, "contrast", 100)),
                    Brightness = (int)Math.Round(D(body, "brightness", 0)),
                    Temperature = (int)Math.Round(D(body, "temperature", 6500)),
                    Saturation = (int)Math.Round(D(body, "saturation", 100)),
                }.Clamp();
                var old = S(body, "old");
                lock (AppConfig.Gate)
                {
                    var i = cfg.ColorProfiles.FindIndex(c => string.Equals(c.Name, old ?? p.Name, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) i = cfg.ColorProfiles.FindIndex(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (i == 0) return R(false, "Le profil Normal ne se modifie pas");
                    if (i > 0) cfg.ColorProfiles[i] = p;
                    else
                    {
                        if (cfg.ColorProfiles.Count >= 12) return R(false, "12 profils maximum");
                        cfg.ColorProfiles.Add(p);
                    }
                    if (old != null && string.Equals(cfg.ActiveColor, old, StringComparison.OrdinalIgnoreCase)) cfg.ActiveColor = p.Name;
                    foreach (var g in cfg.Games)
                        if (old != null && string.Equals(g.Color, old, StringComparison.OrdinalIgnoreCase)) g.Color = p.Name;
                }
                cfg.Save();
                if (B(body, "apply") ?? true)
                {
                    var (ok, msg) = Tweaks.SetColor(cfg, p.Name);
                    return R(ok, B(body, "quiet") == true && ok ? null : msg);
                }
                return R(true, $"Profil {p.Name} enregistré");
            }
            case "color_preview":
            {
                // aperçu en direct pendant qu'on bouge les curseurs (rien n'est enregistré)
                var p = new ColorProfile
                {
                    Name = S(body, "name") ?? "Aperçu",
                    Gamma = D(body, "gamma", 1),
                    Contrast = (int)Math.Round(D(body, "contrast", 100)),
                    Brightness = (int)Math.Round(D(body, "brightness", 0)),
                    Temperature = (int)Math.Round(D(body, "temperature", 6500)),
                    Saturation = (int)Math.Round(D(body, "saturation", 100)),
                };
                var (ok, msg) = ScreenColor.Apply(p);
                return R(ok, ok ? null : msg);
            }
            case "color_delete":
            {
                var name = S(body, "name");
                bool wasActive;
                lock (AppConfig.Gate)
                {
                    var i = cfg.ColorProfiles.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (i <= 0) return R(false, i == 0 ? "Le profil Normal ne se supprime pas" : "Profil introuvable");
                    cfg.ColorProfiles.RemoveAt(i);
                    wasActive = string.Equals(cfg.ActiveColor, name, StringComparison.OrdinalIgnoreCase);
                    foreach (var g in cfg.Games)
                        if (string.Equals(g.Color, name, StringComparison.OrdinalIgnoreCase)) g.Color = null;
                }
                cfg.Save();
                if (wasActive) Tweaks.SetColor(cfg, ScreenColor.NormalName);
                return R(true, $"Profil {name} supprimé");
            }

            // ---------------------------------------------------------------- jeu
            case "winkey_set":
            {
                var on = B(body, "on") ?? !cfg.WinKeyLock;
                var (ok, msg) = Tweaks.SetWinKey(cfg, on);
                if (ok) Osd.Toggle("Touche Windows", on ? "Bloquée" : "Débloquée", on);
                return R(ok, msg);
            }
            case "capture":
            {
                var (ok, msg) = WinKey.Capture(S(body, "what"), S(body, "tool"));
                return R(ok, msg);
            }
            case "game_save":
            {
                var exe = (S(body, "exe") ?? "").Trim();
                if (GameProfile.Norm(exe).Length == 0) return R(false, "Indique le programme du jeu (ex. cod.exe)");
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                var name = (S(body, "name") ?? "").Trim();
                if (name.Length == 0) name = exe[..^4];
                var mode = S(body, "mode");
                var color = S(body, "color");
                var g = new GameProfile
                {
                    Exe = Cut(exe, 80),
                    Name = Cut(name, 40),
                    Enabled = B(body, "enabled") ?? true,
                    Mode = ModeDefs.Known(mode) ? mode : null,
                    Gpu = B(body, "gpu"),
                    Color = string.IsNullOrWhiteSpace(color) ? null : color,
                    WinKey = B(body, "winkey") ?? false,
                    CpuProfile = ModeDefs.Known(S(body, "cpu")) ? S(body, "cpu") : null,
                    GpuCore = Has(body, "gpu_core") && !IsNull(body, "gpu_core") ? Math.Clamp((int)D(body, "gpu_core", 0), -500, 1000) : null,
                    GpuMem = Has(body, "gpu_mem") && !IsNull(body, "gpu_mem") ? Math.Clamp((int)D(body, "gpu_mem", 0), -2000, 3000) : null,
                };
                var old = S(body, "old");
                lock (AppConfig.Gate)
                {
                    var i = cfg.Games.FindIndex(x => GameProfile.Norm(x.Exe) == GameProfile.Norm(old ?? exe));
                    if (i < 0) i = cfg.Games.FindIndex(x => GameProfile.Norm(x.Exe) == GameProfile.Norm(exe));
                    if (i >= 0) cfg.Games[i] = g;
                    else
                    {
                        if (cfg.Games.Count >= 30) return R(false, "30 jeux maximum");
                        cfg.Games.Add(g);
                    }
                }
                cfg.Save();
                return R(true, $"Profil {g.Name} enregistré : il s'appliquera au prochain lancement du jeu");
            }
            case "game_remove":
            {
                var exe = GameProfile.Norm(S(body, "exe"));
                int n;
                lock (AppConfig.Gate) n = cfg.Games.RemoveAll(x => GameProfile.Norm(x.Exe) == exe);
                cfg.Save();
                return R(n > 0, n > 0 ? "Profil de jeu supprimé" : "Profil introuvable");
            }

            // ---------------------------------------------------------------- processeur (remplace ThrottleStop)
            case "cpu_enable":
            {
                var on = B(body, "on") ?? !cfg.Cpu.Enabled;
                cfg.Cpu.Enabled = on;
                cfg.Save();
                if (on) System.Threading.Tasks.Task.Run(Cpu.Reapply);
                return R(true, on ? "Keystone gère le processeur" : "Keystone ne touche plus au processeur (au prochain démarrage)");
            }
            case "cpu_uv_test":
            {
                var mode = S(body, "mode");
                if (!ModeDefs.Known(mode)) mode = cfg.Cpu.Active;
                string extra = "";
                if (mode != cfg.Cpu.Active)
                {
                    // le test se fait avec le profil concerné : on l'active d'abord
                    Cpu.TestUndervolt(mode, I(body, "core"), I(body, "gpu"), I(body, "sa"));
                    Cpu.SetProfile(mode);
                    extra = $" (profil {CpuTuner.ProfileLabel(mode)} activé)";
                }
                var (ok, msg) = Cpu.TestUndervolt(mode, I(body, "core"), I(body, "gpu"), I(body, "sa"));
                return R(ok, msg + extra);
            }
            case "stress_start":
            {
                if (Cpu.Stress == null) return R(false, "Test indisponible");
                var (ok, msg) = Cpu.Stress.Start(I(body, "minutes"), cfg.Cpu.Active, Cpu.ActiveCore);
                return R(ok, msg);
            }
            case "stress_stop":
            {
                if (Cpu.Stress == null) return R(false, "Test indisponible");
                var (ok, msg) = Cpu.Stress.Stop();
                return R(ok, msg);
            }
            case "cpu_profile":
            {
                var id = S(body, "id");
                var (ok, msg) = Cpu.SetProfile(id);
                if (ok) Osd.CpuProfile(id);
                return R(ok, msg);
            }
            case "cpu_link":
            {
                cfg.Cpu.Linked = B(body, "on") ?? !cfg.Cpu.Linked;
                cfg.Save();
                if (cfg.Cpu.Linked) System.Threading.Tasks.Task.Run(() => Cpu.OnFanModeChanged(cfg.LastMode));
                return R(true, cfg.Cpu.Linked ? "Le profil CPU suit la ventilation" : "Profil CPU et ventilation séparés");
            }
            case "cpu_uv_set":
            {
                // recopie de valeurs déjà gardées sur un autre profil
                var mode = S(body, "mode");
                if (!ModeDefs.Known(mode)) return R(false, "Mode inconnu");
                var t = Cpu.ModeTune(mode);
                lock (AppConfig.Gate)
                {
                    t.CoreMv = I(body, "core");
                    t.GpuMv = I(body, "gpu");
                    t.SaMv = I(body, "sa");
                    t.Clamp();
                }
                cfg.Save();
                if (mode == cfg.Cpu.Active && cfg.Cpu.Enabled) Cpu.ApplyUndervolt(t.CoreMv, t.GpuMv, t.SaMv);
                return R(true, "Undervolt enregistré");
            }
            case "cpu_uv_keep":
            {
                var (ok, msg) = Cpu.KeepTest();
                return R(ok, msg);
            }
            case "cpu_uv_cancel":
            {
                var (ok, msg) = Cpu.CancelTest();
                return R(ok, msg);
            }
            case "cpu_mode_save":
            {
                var mode = S(body, "mode");
                if (!ModeDefs.Known(mode)) return R(false, "Mode inconnu");
                var t = Cpu.ModeTune(mode);
                lock (AppConfig.Gate)
                {
                    if (B(body, "limits") is bool lim) t.Limits = lim;
                    if (Has(body, "pl1")) t.Pl1 = I(body, "pl1");
                    if (Has(body, "pl2")) t.Pl2 = I(body, "pl2");
                    if (Has(body, "tau")) t.Tau = I(body, "tau");
                    if (Has(body, "epp")) t.EppRaw = I(body, "epp");
                    if (Has(body, "max_mhz")) t.MaxMhz = Math.Max(0, I(body, "max_mhz"));
                    t.Clamp();
                    if (Has(body, "boost")) t.Boost = Math.Clamp(I(body, "boost"), 0, 2);
                }
                cfg.Save();
                if (mode != cfg.Cpu.Active) return R(true, $"Profil {CpuTuner.ProfileLabel(mode)} enregistré");
                if (!cfg.Cpu.Enabled) return R(true, "Enregistré. Active « Keystone gère le processeur » pour l'appliquer");
                if (t.Limits && (B(body, "limits") == true || Has(body, "pl1") || Has(body, "pl2") || Has(body, "tau")))
                {
                    var (ok, msg) = CpuTuner.WriteLimits(t.Pl1, t.Pl2, t.Tau);
                    if (!ok) return R(false, msg);
                }
                if (Has(body, "epp") || Has(body, "max_mhz") || Has(body, "boost")) CpuTuner.ApplyPowerPlan(t.EppRaw, t.MaxMhz, t.Boost);
                return R(true, "Réglages processeur appliqués");
            }
            case "cpu_tsboot":
            {
                var on = B(body, "on") ?? !cfg.Cpu.TsBoot;
                cfg.Cpu.TsBoot = on;
                cfg.Save();
                if (on) System.Threading.Tasks.Task.Run(() => Cpu.EnsureThreshold(true));
                return R(true, on ? "Seuil à 100 °C : ThrottleStop sera lancé caché quelques secondes au démarrage" : "Seuil thermique laissé au BIOS (95 °C)");
            }
            case "temps_reset":
                ResetPeaks?.Invoke();
                return R(true, "Températures max et min remises à zéro");
            case "touchpad_set":
            {
                var on = B(body, "on") ?? !Touchpad.State().enabled;
                var (ok, msg) = Touchpad.Set(on);
                if (ok) Osd.Toggle("Pavé tactile", on ? "Activé" : "Désactivé", on);
                return R(ok, msg);
            }
            case "cpu_takeover":
            {
                var (ok, msg) = Cpu.TakeOver();
                return R(ok, msg);
            }

            // ---------------------------------------------------------------- minuteur
            case "timer_set":
            {
                var (ok, msg) = Tweaks.SetTimer(I(body, "minutes"), S(body, "what"));
                return R(ok, msg);
            }

            case "lock":
                Win.LockWorkStation();
                return R(true, "PC verrouillé");
            case "sleep":
                Task.Run(async () => { await Task.Delay(1000); Win.Sleep(); }); // laisse partir la réponse
                return R(true, "Mise en veille…");
            case "restart":
                Win.Run("shutdown", "/r /t 5", 5000);
                return R(true, "Redémarrage dans 5 s");
            case "shutdown":
                Win.Run("shutdown", "/s /t 5", 5000);
                return R(true, "Arrêt dans 5 s");
        }
        return R(false, "Action inconnue");
    }

    AppEntry AppAt(int i)
    {
        lock (AppConfig.Gate)
            return i >= 0 && i < cfg.Apps.Count ? cfg.Apps[i] : null;
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
