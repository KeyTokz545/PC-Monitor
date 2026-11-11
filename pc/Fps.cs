// Compteur d'images par seconde sans rien injecter dans le jeu : on écoute les événements « Present »
// que DirectX (DXGI / Direct3D 9) publie dans le journal d'événements de Windows (ETW), comme PresentMon.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PCMonitor;

public static class Fps
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3d9PresentStart = 1;
    const string SessionName = "PCMonitor-FPS";

    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "PCMonitor", "explorer", "System", "Idle", "csrss", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "LockApp", "Widgets", "ApplicationFrameHost",
    };

    sealed class Track
    {
        public string Name;
        public double LastTs = -1;      // horodatage du dernier « Present » (ms, horloge ETW)
        public long LastWall;           // dernier événement reçu (ms, horloge du PC)
        public readonly Queue<(double Ts, float Ft)> Frames = new();
    }

    static readonly object Gate = new();
    static readonly Dictionary<int, Track> Tracks = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<int> History = new();
    static TraceEventSession session;
    static Thread thread;
    static Timer timer;
    static long lastTouch, lastFail = -999_999;
    static int currentPid;
    static string error;

    /// <summary>Processus du jeu détecté par les profils de jeu (prioritaire).</summary>
    public static int PreferredPid { get; set; }

    // partie en cours (jeu à profil) : histogramme des temps d'image par pas de 0,1 ms, pour le bilan
    static int[] sessionHist;
    static long sessionFrames;
    static double sessionMs;
    static volatile bool keepAlive;

    /// <summary>Début d'une partie : l'écoute reste active jusqu'à la fin, même sans téléphone.</summary>
    public static void BeginSession()
    {
        lock (Gate)
        {
            sessionHist = new int[2001];
            sessionFrames = 0;
            sessionMs = 0;
            keepAlive = true;
        }
        Interlocked.Exchange(ref lastTouch, Clock.ElapsedMilliseconds);
        if (session == null) Start();
    }

    /// <summary>Fin de partie : FPS moyen, 1 % et 0,1 % bas sur toute la partie (null si rien mesuré).</summary>
    public static Dictionary<string, object> EndSession()
    {
        int[] h;
        long frames;
        double ms;
        lock (Gate)
        {
            h = sessionHist;
            frames = sessionFrames;
            ms = sessionMs;
            sessionHist = null;
            keepAlive = false;
        }
        if (h == null || frames < 100 || ms <= 0) return null;
        double Low(double share)
        {
            long need = Math.Max(1, (long)(frames * share)), got = 0;
            double sum = 0;
            for (int i = h.Length - 1; i >= 0 && got < need; i--)
            {
                long take = Math.Min(h[i], need - got);
                sum += take * (i / 10.0 + 0.05);
                got += take;
            }
            return got > 0 ? 1000.0 / (sum / got) : 0;
        }
        return new Dictionary<string, object>
        {
            ["fps_avg"] = (int)Math.Round(frames / (ms / 1000.0)),
            ["low1"] = (int)Math.Round(Low(0.01)),
            ["low01"] = (int)Math.Round(Low(0.001)),
            ["frames"] = frames,
        };
    }

    public static bool Running => session != null;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return (int)pid;
    }

    /// <summary>Le téléphone regarde les FPS : on démarre l'écoute (elle s'arrête seule 60 s après).</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref lastTouch, Clock.ElapsedMilliseconds);
        if (session == null && Clock.ElapsedMilliseconds - lastFail > 30_000) Start();
    }

    static void Start()
    {
        lock (Gate)
        {
            if (session != null) return;
            try
            {
                var s = new TraceEventSession(SessionName) { StopOnDispose = true };
                s.Source.AllEvents += OnEvent;
                s.EnableProvider(Dxgi, TraceEventLevel.Verbose, ulong.MaxValue);
                s.EnableProvider(D3d9, TraceEventLevel.Verbose, ulong.MaxValue);
                session = s;
                error = null;
                Tracks.Clear();
                History.Clear();
                thread = new Thread(() =>
                {
                    try { s.Source.Process(); }
                    catch (Exception e) { Log.Error("Compteur FPS", e); }
                }) { IsBackground = true, Name = "FPS" };
                thread.Start();
                timer = new Timer(_ => Second(), null, 1000, 1000);
                Log.Info("Compteur FPS démarré");
            }
            catch (Exception e)
            {
                lastFail = Clock.ElapsedMilliseconds;
                error = "Compteur FPS indisponible sur ce PC (voir le journal).";
                Log.Error("Démarrage du compteur FPS", e);
                try { session?.Dispose(); } catch { }
                session = null;
            }
        }
    }

    public static void Stop()
    {
        TraceEventSession s;
        Timer t;
        lock (Gate)
        {
            s = session;
            t = timer;
            session = null;
            timer = null;
            Tracks.Clear();
            History.Clear();
            currentPid = 0;
        }
        // arrêt hors du verrou : le fil d'écoute peut encore attendre ce verrou
        try { t?.Dispose(); } catch { }
        try { s?.Dispose(); } catch { }
    }

    static void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && (id == DxgiPresentStart || id == DxgiPresentMpoStart))
                       || (e.ProviderGuid == D3d9 && id == D3d9PresentStart);
        if (!present) return;
        int pid = e.ProcessID;
        if (pid <= 4) return;
        double ts = e.TimeStampRelativeMSec;
        long wall = Clock.ElapsedMilliseconds;
        lock (Gate)
        {
            if (!Tracks.TryGetValue(pid, out var t))
            {
                t = new Track { Name = NameOf(pid) };
                Tracks[pid] = t;
            }
            if (t.LastTs >= 0)
            {
                var ft = ts - t.LastTs;
                if (ft > 0 && ft < 1000)
                {
                    t.Frames.Enqueue((ts, (float)ft));
                    if (sessionHist != null && pid == PreferredPid)
                    {
                        sessionHist[Math.Min(2000, (int)(ft * 10))]++;
                        sessionFrames++;
                        sessionMs += ft;
                    }
                }
            }
            t.LastTs = ts;
            t.LastWall = wall;
            while (t.Frames.Count > 0 && (t.Frames.Count > 6000 || ts - t.Frames.Peek().Ts > 10_000)) t.Frames.Dequeue();
        }
    }

    static string NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    static bool Active(Track t, long now) => now - t.LastWall < 2500 && t.Frames.Count > 2;

    /// <summary>Choisit l'appli à mesurer : le jeu du profil, sinon la fenêtre au premier plan, sinon la plus rapide.</summary>
    static int Pick(long now)
    {
        if (PreferredPid > 0 && Tracks.TryGetValue(PreferredPid, out var p) && Active(p, now)) return PreferredPid;
        var fg = ForegroundPid();
        if (fg > 0 && Tracks.TryGetValue(fg, out var f) && Active(f, now) && !Ignored.Contains(f.Name)) return fg;
        if (currentPid > 0 && Tracks.TryGetValue(currentPid, out var c) && Active(c, now) && !Ignored.Contains(c.Name)) return currentPid;
        int best = 0, bestCount = 0;
        foreach (var (pid, t) in Tracks)
        {
            if (!Active(t, now) || Ignored.Contains(t.Name) || string.IsNullOrEmpty(t.Name)) continue;
            var n = t.Frames.Count(fr => t.LastTs - fr.Ts <= 1000);
            if (n > bestCount) { best = pid; bestCount = n; }
        }
        return best;
    }

    static int FpsOf(Track t) => t.Frames.Count(fr => t.LastTs - fr.Ts <= 1000);

    /// <summary>Une fois par seconde : historique, ménage, arrêt si plus personne ne regarde.</summary>
    static void Second()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref lastTouch) > 60_000 && !keepAlive)
        {
            Stop();
            Log.Info("Compteur FPS arrêté (plus personne ne regarde)");
            return;
        }
        lock (Gate)
        {
            foreach (var pid in Tracks.Where(kv => now - kv.Value.LastWall > 30_000).Select(kv => kv.Key).ToList())
                Tracks.Remove(pid);
            var pick = Pick(now);
            if (pick != currentPid) History.Clear();
            currentPid = pick;
            if (pick > 0)
            {
                History.Add(FpsOf(Tracks[pick]));
                if (History.Count > 120) History.RemoveAt(0);
            }
        }
    }

    /// <summary>Mesures pour le téléphone.</summary>
    public static Dictionary<string, object> State()
    {
        Touch();
        var d = new Dictionary<string, object> { ["running"] = Running, ["error"] = error };
        lock (Gate)
        {
            long now = Clock.ElapsedMilliseconds;
            var pid = currentPid > 0 && Tracks.ContainsKey(currentPid) && Active(Tracks[currentPid], now) ? currentPid : Pick(now);
            if (pid <= 0 || !Tracks.TryGetValue(pid, out var t))
            {
                d["app"] = null;
                return d;
            }
            var frames = t.Frames.ToArray();
            var lastSec = frames.Where(fr => t.LastTs - fr.Ts <= 1000).Select(fr => fr.Ft).ToArray();
            int fps = lastSec.Length;
            var sorted = frames.Select(fr => fr.Ft).OrderByDescending(x => x).ToArray();
            int worst = Math.Max(1, sorted.Length / 100);
            double low1 = sorted.Length >= 20 ? 1000.0 / sorted.Take(worst).Average() : fps;
            double low01 = sorted.Length >= 100 ? 1000.0 / sorted.Take(Math.Max(1, sorted.Length / 1000)).Average() : low1;
            d["app"] = t.Name;
            d["pid"] = pid;
            d["fps"] = fps;
            d["low1"] = (int)Math.Round(low1);
            d["low01"] = (int)Math.Round(low01);
            d["avg"] = sorted.Length > 0 ? (int)Math.Round(1000.0 / sorted.Average()) : 0;
            d["frametime"] = lastSec.Length > 0 ? (object)Math.Round(lastSec.Average(), 1) : null;
            d["frametime_max"] = lastSec.Length > 0 ? (object)Math.Round((double)lastSec.Max(), 1) : null;
            d["frames"] = frames.Skip(Math.Max(0, frames.Length - 180)).Select(fr => Math.Round(fr.Ft, 1)).ToList();
            d["history"] = History.ToList();
        }
        return d;
    }
}
