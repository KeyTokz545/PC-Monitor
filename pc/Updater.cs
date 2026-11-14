// Mise à jour en un bouton : la dernière compilation publiée sur GitHub (Releases → latest) est téléchargée,
// vérifiée, mise à la place de Keystone.exe, puis Keystone redémarre tout seul.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCMonitor;

public static class Updater
{
    const string Repo = "KeyTokz545/PC-Monitor";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly object Gate = new();
    static AppConfig cfg;
    static string assetUrl;
    static long assetSize;
    static bool announced;

    /// <summary>idle, checking, ready (nouvelle version), uptodate, downloading, installing, error</summary>
    public static string State { get; private set; } = "idle";
    public static string Latest { get; private set; }
    public static string Error { get; private set; }
    public static int Progress { get; private set; }
    public static DateTime? CheckedAt { get; private set; }

    /// <summary>Ferme Keystone proprement (fourni par TrayApp).</summary>
    public static Action Quit { get; set; }
    /// <summary>Prévient qu'une nouvelle version est disponible (bulle près de l'horloge).</summary>
    public static Action<string> Announce { get; set; }

    public static void Init(AppConfig config)
    {
        cfg = config;
        // restes d'une mise à jour précédente
        Task.Run(async () =>
        {
            await Task.Delay(8000);
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
                foreach (var f in Directory.GetFiles(dir, "Keystone.old*.exe")) { try { File.Delete(f); } catch { } }
                var tmp = Path.Combine(dir, "Keystone.new.exe");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        });
    }

    public static bool HasToken => !string.IsNullOrWhiteSpace(cfg?.Update?.Token);

    static HttpRequestMessage Req(string url, bool binary)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.UserAgent.ParseAdd("Keystone/" + Program.Version);
        r.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
        // le jeton n'est envoyé qu'à GitHub : HttpClient le retire quand GitHub redirige vers le fichier
        if (HasToken) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Update.Token.Trim());
        return r;
    }

    static bool Newer(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va > vb;

    /// <summary>Regarde si une version plus récente est publiée.</summary>
    public static async Task Check()
    {
        lock (Gate)
        {
            if (State is "checking" or "downloading" or "installing") return;
            State = "checking";
            Error = null;
        }
        try
        {
            using var res = await Http.SendAsync(Req($"https://api.github.com/repos/{Repo}/releases/tags/latest", false));
            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new Exception(HasToken
                    ? "Le jeton GitHub n'a pas accès au dépôt PC-Monitor."
                    : "Le dépôt GitHub est privé : ajoute un jeton GitHub (lecture seule) ou rends le dépôt public.");
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new Exception("GitHub refuse le jeton : il est peut-être expiré ou n'a pas accès au dépôt.");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var m = Regex.Match(name, @"(\d+\.\d+\.\d+)");
            string url = null;
            long size = 0;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString() == "Keystone.exe")
                {
                    url = a.GetProperty("url").GetString();
                    size = a.GetProperty("size").GetInt64();
                }
            lock (Gate)
            {
                Latest = m.Success ? m.Groups[1].Value : null;
                assetUrl = url;
                assetSize = size;
                CheckedAt = DateTime.Now;
                State = Latest != null && url != null && Newer(Latest, Program.Version) ? "ready" : "uptodate";
            }
            if (State == "ready" && !announced)
            {
                announced = true;
                Log.Info($"Mise à jour disponible : {Latest}");
                Announce?.Invoke(Latest);
            }
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                Error = e is HttpRequestException or TaskCanceledException
                    ? "GitHub injoignable : vérifie la connexion internet du PC."
                    : e.Message;
                State = "error";
                CheckedAt = DateTime.Now;
            }
            Log.Info("Mise à jour : " + Error);
        }
    }

    /// <summary>Télécharge et installe la version trouvée, puis relance Keystone.</summary>
    public static (bool Ok, string Message) Install()
    {
        lock (Gate)
        {
            if (State != "ready" || assetUrl == null) return (false, "Aucune mise à jour à installer : recherche d'abord.");
            State = "downloading";
            Progress = 0;
            Error = null;
        }
        var target = Latest;
        Task.Run(async () =>
        {
            var exe = Environment.ProcessPath;
            var dir = Path.GetDirectoryName(exe);
            var tmp = Path.Combine(dir, "Keystone.new.exe");
            try
            {
                using (var res = await Http.SendAsync(Req(assetUrl, true), HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    var total = res.Content.Headers.ContentLength ?? assetSize;
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(tmp);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read));
                        done += read;
                        if (total > 0) Progress = (int)Math.Min(100, done * 100 / total);
                    }
                }
                // vérifications : taille, exécutable Windows, bonne version
                var len = new FileInfo(tmp).Length;
                if (assetSize > 0 && len != assetSize) throw new Exception("téléchargement incomplet, réessaie.");
                using (var f = File.OpenRead(tmp))
                    if (f.ReadByte() != 'M' || f.ReadByte() != 'Z') throw new Exception("le fichier reçu n'est pas un programme Windows.");
                var ver = FileVersionInfo.GetVersionInfo(tmp).FileVersion ?? "";
                if (!ver.StartsWith(target)) throw new Exception($"version inattendue ({ver}).");

                State = "installing";
                Log.Info($"Mise à jour : {Program.Version} → {target}, remplacement de {exe}");
                // un programme lancé peut être renommé (pas remplacé) : l'ancien part de côté, le nouveau prend sa place
                var old = Path.Combine(dir, "Keystone.old.exe");
                try { if (File.Exists(old)) File.Delete(old); }
                catch { old = Path.Combine(dir, $"Keystone.old{Environment.TickCount64}.exe"); }
                try
                {
                    File.Move(exe, old);
                    try { File.Move(tmp, exe); }
                    catch { File.Move(old, exe); throw; }
                    Process.Start(new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = true, WorkingDirectory = dir });
                }
                catch (Exception moveError)
                {
                    // repli : une petite commande remplace le fichier une fois Keystone fermé, puis le relance
                    Log.Info("Mise à jour : renommage impossible (" + moveError.Message + "), remplacement après fermeture");
                    var cmd = $"/c ping -n 5 127.0.0.1 >nul & move /y \"{tmp}\" \"{exe}\" & start \"\" \"{exe}\"";
                    Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = dir });
                }
                await Task.Delay(400);
                Quit?.Invoke();
            }
            catch (Exception e)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                lock (Gate)
                {
                    Error = "Mise à jour impossible : " + (e is HttpRequestException or TaskCanceledException ? "téléchargement interrompu." : e.Message);
                    State = "ready";
                }
                Log.Error("Mise à jour", e);
            }
        });
        return (true, $"Téléchargement de Keystone {target}…");
    }

    public static void SetToken(string token)
    {
        cfg.Update.Token = (token ?? "").Trim();
        cfg.Save();
        announced = false;
    }
}
