using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace PCMonitor;

/// <summary>Relève les mesures une fois par seconde et garde la dernière photo prête à envoyer.</summary>
public sealed class Collector
{
    readonly AppConfig cfg;
    readonly Sensors sensors;
    readonly Asus asus;
    readonly Queue<int?> latencies = new();
    Dictionary<string, object> wifi;
    volatile string statsJson = "{}";
    Thread thread;
    volatile bool running;

    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public float? CpuTemp { get; private set; }
    public float? GpuTemp { get; private set; }
    public float? CpuPower { get; private set; }
    public float? GpuPower { get; private set; }

    /// <summary>Jeu à profil automatique en cours (nom + secondes depuis son lancement), pour le HUD du téléphone.</summary>
    public Func<object> GameInfo { get; set; }

    // max / min depuis la dernière remise à zéro (gardés par le PC, communs à tous les téléphones)
    readonly object peakGate = new();
    float? cpuMin, cpuMax, gpuMin, gpuMax;
    DateTime peaksSince = DateTime.Now;

    // veille : sans téléphone connecté, mesures toutes les 5 s, sans Wi-Fi ni ping
    long lastClient = -60_000;
    bool trimmed;

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>Un téléphone regarde : mesures complètes chaque seconde.</summary>
    public void Touch() => Interlocked.Exchange(ref lastClient, Environment.TickCount64);

    public bool Active => Environment.TickCount64 - Interlocked.Read(ref lastClient) < 15_000;

    public void ResetPeaks()
    {
        lock (peakGate)
        {
            cpuMin = cpuMax = gpuMin = gpuMax = null;
            peaksSince = DateTime.Now;
        }
        Log.Info("Températures max et min remises à zéro");
    }

    public Collector(AppConfig cfg, Sensors sensors, Asus asus)
    {
        this.cfg = cfg;
        this.sensors = sensors;
        this.asus = asus;
    }

    public string StatsJson => statsJson;

    public void Start()
    {
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "Mesures" };
        thread.Start();
    }

    public void Stop() => running = false;

    void Loop()
    {
        DateTime lastSlow = DateTime.MinValue, lastLatency = DateTime.MinValue;
        long idleSince = Environment.TickCount64;
        while (running)
        {
            var t0 = DateTime.UtcNow;
            var active = Active;
            try
            {
                if (active)
                {
                    trimmed = false;
                    idleSince = Environment.TickCount64;
                    if ((t0 - lastSlow).TotalSeconds >= 5)
                    {
                        lastSlow = t0;
                        wifi = NetInfo.Wifi();
                    }
                    if ((t0 - lastLatency).TotalSeconds >= 2)
                    {
                        lastLatency = t0;
                        latencies.Enqueue(NetInfo.Latency(cfg.LatencyHost, cfg.LatencyPort));
                        while (latencies.Count > 30) latencies.Dequeue();
                    }
                }
                Snapshot(active);
                // une minute sans téléphone : on rend la mémoire inutilisée à Windows
                if (!active && !trimmed && Environment.TickCount64 - idleSince > 60_000)
                {
                    trimmed = true;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch (Exception e)
            {
                Log.Error("Mesures", e);
            }
            var period = active ? 1000 : 5000;
            var wait = period - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            // réveil rapide dès qu'un téléphone revient
            for (var left = Math.Max(200, wait); left > 0 && running; left -= 250)
            {
                Thread.Sleep(Math.Min(250, left));
                if (!active && Active) break;
            }
        }
    }

    static void Peak(ref float? min, ref float? max, float? v)
    {
        if (!v.HasValue || v.Value <= 1) return;
        min = min.HasValue ? Math.Min(min.Value, v.Value) : v.Value;
        max = max.HasValue ? Math.Max(max.Value, v.Value) : v.Value;
    }

    void Snapshot(bool full)
    {
        var s = sensors.Read(wifi?.GetValueOrDefault("name") as string, full);

        // latence, gigue, pertes
        var lats = latencies.ToList();
        var ok = lats.Where(x => x.HasValue).Select(x => x.Value).ToList();
        var net = (Dictionary<string, object>)s["net"];
        net["latency"] = lats.Count > 0 ? lats[^1] : null;
        net["jitter"] = ok.Count > 2 ? (object)(int)Math.Round(ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average()) : null;
        net["loss"] = lats.Count > 0 ? (object)(int)Math.Round(100.0 * (lats.Count - ok.Count) / lats.Count) : null;
        net["history"] = lats;
        net["wifi"] = wifi;

        // ventilateurs (lus par le pilote ASUS)
        var fans = new List<object>();
        if (asus.Connected)
        {
            int c = asus.FanRpm(false), g = asus.FanRpm(true);
            if (c >= 0) fans.Add(new Dictionary<string, object> { ["name"] = "Ventilo CPU", ["rpm"] = c });
            if (g >= 0) fans.Add(new Dictionary<string, object> { ["name"] = "Ventilo GPU", ["rpm"] = g });
        }
        s["fans"] = fans;

        var ps = SystemInformation.PowerStatus;
        bool? ac = ps.PowerLineStatus switch { PowerLineStatus.Online => true, PowerLineStatus.Offline => false, _ => null };
        int? battery = ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) ? null : (int)Math.Round(ps.BatteryLifePercent * 100);

        s["pc"] = Environment.MachineName;
        s["uptime"] = Environment.TickCount64 / 1000;
        s["power"] = new Dictionary<string, object> { ["ac"] = ac, ["battery"] = battery };
        s["mode"] = new Dictionary<string, object>
        {
            ["current"] = string.IsNullOrEmpty(cfg.LastMode) ? null : cfg.LastMode,
            ["source"] = "pc",
            ["control_ok"] = asus.Connected,
        };
        s["lhm"] = new Dictionary<string, object> { ["ok"] = sensors.Ok, ["error"] = sensors.Error };
        s["cpu_profile"] = cfg.Cpu.Enabled ? cfg.Cpu.Active : null;
        s["last_report"] = GameReports.LatestId;
        try { s["game"] = GameInfo?.Invoke(); } catch { s["game"] = null; }
        try { s["throttle"] = Throttle.Read(CpuTemp); } catch { }
        s["demo"] = false;
        s["app"] = "pcmonitor";
        s["version"] = Program.Version;
        s["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var cpuD = (Dictionary<string, object>)s["cpu"];
        var gpuD = (Dictionary<string, object>)s["gpu"];
        CpuTemp = cpuD.GetValueOrDefault("temp") as float?;
        GpuTemp = gpuD.GetValueOrDefault("temp") as float?;
        CpuPower = cpuD.GetValueOrDefault("power") as float?;
        GpuPower = gpuD.GetValueOrDefault("power") as float?;
        lock (peakGate)
        {
            Peak(ref cpuMin, ref cpuMax, CpuTemp);
            Peak(ref gpuMin, ref gpuMax, GpuTemp);
            cpuD["temp_min"] = cpuMin;
            cpuD["temp_max"] = cpuMax;
            gpuD["temp_min"] = gpuMin;
            gpuD["temp_max"] = gpuMax;
            s["peaks_since"] = (long)(DateTime.Now - peaksSince).TotalSeconds;
        }
        statsJson = JsonSerializer.Serialize(s, Json);
    }
}
