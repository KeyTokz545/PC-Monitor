using System;
using System.Collections.Generic;
using System.Linq;
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
        while (running)
        {
            var t0 = DateTime.UtcNow;
            try
            {
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
                Snapshot();
            }
            catch (Exception e)
            {
                Log.Error("Mesures", e);
            }
            var wait = 1000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            Thread.Sleep(Math.Max(200, wait));
        }
    }

    void Snapshot()
    {
        var s = sensors.Read(wifi?.GetValueOrDefault("name") as string);

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
        s["demo"] = false;
        s["app"] = "pcmonitor";
        s["version"] = Program.Version;
        s["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        CpuTemp = ((Dictionary<string, object>)s["cpu"])["temp"] as float?;
        GpuTemp = ((Dictionary<string, object>)s["gpu"])["temp"] as float?;
        statsJson = JsonSerializer.Serialize(s, Json);
    }
}
