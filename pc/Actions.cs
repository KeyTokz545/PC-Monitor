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

    public Actions(AppConfig cfg, ModeService modes)
    {
        this.cfg = cfg;
        this.modes = modes;
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
                var (ok, msg) = modes.Apply(S(body, "mode"));
                return R(ok, msg);
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
