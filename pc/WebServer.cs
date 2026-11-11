using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PCMonitor;

/// <summary>Serveur web du tableau de bord (même adresse et même API que la version Python).</summary>
public sealed class WebServer
{
    readonly AppConfig cfg;
    readonly Collector collector;
    readonly Actions actions;
    readonly Controls controls;
    HttpListener listener;

    static readonly byte[] Page = Resource("dashboard.html");
    static readonly byte[] IconPng = Resource("icon.png");

    public WebServer(AppConfig cfg, Collector collector, Actions actions, Controls controls)
    {
        this.cfg = cfg;
        this.collector = collector;
        this.actions = actions;
        this.controls = controls;
    }

    public static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Start()
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{cfg.Port}/");
        listener.Start();
        Log.Info($"Serveur web sur le port {cfg.Port}");
        Task.Run(Loop);
    }

    public void Stop()
    {
        try { listener?.Stop(); listener?.Close(); } catch { }
    }

    async Task Loop()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    static void Send(HttpListenerContext ctx, int code, byte[] body, string type)
    {
        try
        {
            var r = ctx.Response;
            r.StatusCode = code;
            r.ContentType = type;
            r.Headers["Cache-Control"] = "no-store";
            r.ContentLength64 = body.Length;
            r.OutputStream.Write(body, 0, body.Length);
            r.OutputStream.Close();
        }
        catch
        {
            // le téléphone a coupé la connexion
        }
    }

    static void SendJson(HttpListenerContext ctx, int code, object obj) =>
        Send(ctx, code, Encoding.UTF8.GetBytes(obj is string s ? s : JsonSerializer.Serialize(obj, Collector.Json)), "application/json; charset=utf-8");

    bool Authorized(HttpListenerContext ctx)
    {
        var key = ctx.Request.QueryString["k"] ?? ctx.Request.Headers["X-Key"] ?? "";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(cfg.Token ?? ""));
    }

    public object Hello() => new Dictionary<string, object>
    {
        ["app"] = "pcmonitor",
        ["name"] = Environment.MachineName,
        ["version"] = Program.Version,
        ["port"] = cfg.Port,
        ["macs"] = NetInfo.Macs(),
    };

    void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (path is "/icon.png" or "/apple-touch-icon.png" or "/favicon.ico")
            {
                Send(ctx, 200, IconPng, "image/png");
                return;
            }
            if (!path.StartsWith("/api/"))
            {
                // toute autre adresse affiche le tableau de bord (adresse mal tapée, etc.)
                Send(ctx, 200, Page, "text/html; charset=utf-8");
                return;
            }
            if (path == "/api/hello")
            {
                SendJson(ctx, 200, Hello());
                return;
            }
            if (!Authorized(ctx))
            {
                SendJson(ctx, 401, new Dictionary<string, object> { ["error"] = "Clé invalide" });
                return;
            }

            switch (path)
            {
                case "/api/stats":
                    collector.Touch();
                    SendJson(ctx, 200, collector.StatsJson);
                    return;

                case "/api/config":
                    List<object> apps;
                    lock (AppConfig.Gate)
                        apps = cfg.Apps.Select(a => (object)new Dictionary<string, object>
                        {
                            ["name"] = a.Name,
                            ["icon"] = a.Icon,
                            ["can_close"] = !string.IsNullOrEmpty(a.Close),
                        }).ToList();
                    SendJson(ctx, 200, new Dictionary<string, object>
                    {
                        ["pc"] = Environment.MachineName,
                        ["modes"] = cfg.Modes.Select(m => new Dictionary<string, object> { ["id"] = m, ["label"] = cfg.ModeLabel(m) }).ToList(),
                        ["apps"] = apps,
                        ["version"] = Program.Version,
                    });
                    return;

                case "/api/controls":
                    SendJson(ctx, 200, controls.State());
                    return;

                case "/api/cpu":
                    SendJson(ctx, 200, actions.Cpu.State());
                    return;

                case "/api/game":
                    SendJson(ctx, 200, Fps.State());
                    return;

                case "/api/procs":
                    SendJson(ctx, 200, new Dictionary<string, object> { ["items"] = GameWatcher.RunningApps() });
                    return;

                case "/api/catalog":
                    if (ctx.Request.QueryString["refresh"] == "1" || (Catalog.Items.Count == 0 && !Catalog.Loading)) Catalog.Refresh();
                    HashSet<string> opens;
                    lock (AppConfig.Gate) opens = cfg.Apps.Select(a => a.Open).ToHashSet();
                    var items = Catalog.Items;
                    SendJson(ctx, 200, new Dictionary<string, object>
                    {
                        ["loading"] = Catalog.Loading,
                        ["items"] = items.Select((it, i) => new Dictionary<string, object>
                        {
                            ["id"] = i,
                            ["name"] = it.Name,
                            ["src"] = it.Src,
                            ["added"] = opens.Contains(it.Open),
                            ["icon"] = Catalog.GuessIcon(it.Name, it.Src),
                        }).ToList(),
                    });
                    return;

                case "/api/action":
                    if (method != "POST")
                    {
                        SendJson(ctx, 405, new Dictionary<string, object> { ["error"] = "POST attendu" });
                        return;
                    }
                    JsonElement body;
                    try
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        var text = reader.ReadToEnd();
                        if (text.Length > 10_000) throw new InvalidDataException();
                        body = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone();
                    }
                    catch
                    {
                        SendJson(ctx, 400, new Dictionary<string, object> { ["ok"] = false, ["msg"] = "Requête illisible" });
                        return;
                    }
                    SendJson(ctx, 200, actions.Do(body));
                    return;
            }
            SendJson(ctx, 404, new Dictionary<string, object> { ["error"] = "Introuvable" });
        }
        catch (Exception e)
        {
            Log.Error("Requête web", e);
            try { SendJson(ctx, 500, new Dictionary<string, object> { ["ok"] = false, ["msg"] = "Erreur sur le PC" }); } catch { }
        }
    }
}
