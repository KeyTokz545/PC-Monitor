using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace PCMonitor;

/// <summary>Capteurs du PC via la bibliothèque de LibreHardwareMonitor (plus besoin de lancer LHM).</summary>
public sealed class Sensors : IDisposable
{
    Computer computer;

    public bool Ok { get; private set; }
    public string Error { get; private set; } = "";

    public void Open()
    {
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
            };
            computer.Open();
            Ok = true;
            Log.Info("Capteurs : " + string.Join(", ", computer.Hardware.Select(h => h.HardwareType + " « " + h.Name + " »")));
        }
        catch (Exception e)
        {
            Ok = false;
            Error = e.Message;
            Log.Error("Ouverture des capteurs", e);
        }
    }

    static float? V(ISensor s) => s?.Value;

    static float? R1(float? v) => v.HasValue ? (float)Math.Round(v.Value, 1) : null;

    static ISensor Pick(IHardware hw, SensorType type, params string[] names)
    {
        if (hw == null) return null;
        var list = hw.Sensors.Where(s => s.SensorType == type && s.Value.HasValue).ToList();
        foreach (var n in names)
        {
            var x = list.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (x != null) return x;
        }
        foreach (var n in names)
        {
            var x = list.FirstOrDefault(s => s.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (x != null) return x;
        }
        return null;
    }

    static readonly Regex CoreClock = new(@"core #\d+", RegexOptions.IgnoreCase);

    /// <summary>Lit tous les capteurs et renvoie le résumé envoyé au téléphone.</summary>
    int fullReads;

    /// <summary>
    /// <paramref name="full"/> = un téléphone regarde : tout est relu (disques une fois sur 5).
    /// Sinon seuls le processeur et la carte graphique sont relus (pour le max / min et l'icône).
    /// </summary>
    public Dictionary<string, object> Read(string wifiAdapterName, bool full = true)
    {
        if (full) fullReads++;
        var result = new Dictionary<string, object>();
        IHardware cpu = null, gpu = null, ram = null;
        var nics = new List<IHardware>();
        var disks = new List<IHardware>();

        if (Ok)
        {
            foreach (var hw in computer.Hardware)
            {
                try
                {
                    var t = hw.HardwareType;
                    bool main = t == HardwareType.Cpu || t == HardwareType.GpuNvidia || t == HardwareType.GpuAmd || t == HardwareType.GpuIntel;
                    bool update = main || (full && (t != HardwareType.Storage || fullReads % 5 == 1));
                    if (update)
                    {
                        hw.Update();
                        foreach (var sub in hw.SubHardware) sub.Update();
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Lecture " + hw.Name, e);
                }
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpu ??= hw;
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        gpu = hw; // carte graphique dédiée en priorité
                        break;
                    case HardwareType.GpuIntel:
                        gpu ??= hw;
                        break;
                    case HardwareType.Memory:
                        if (hw.Name.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) < 0) ram ??= hw;
                        break;
                    case HardwareType.Network:
                        nics.Add(hw);
                        break;
                    case HardwareType.Storage:
                        disks.Add(hw);
                        break;
                }
            }
        }

        // ---- processeur
        var cpuTemp = Pick(cpu, SensorType.Temperature, "CPU Package", "Core Max", "Core (Tctl/Tdie)", "Core Average")
                      ?? cpu?.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).OrderByDescending(s => s.Value).FirstOrDefault();
        var clocks = cpu?.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Value.HasValue && CoreClock.IsMatch(s.Name))
                         .Select(s => s.Value.Value).ToList() ?? new List<float>();
        result["cpu"] = new Dictionary<string, object>
        {
            ["name"] = cpu?.Name ?? "",
            ["temp"] = R1(V(cpuTemp)),
            ["load"] = R1(V(Pick(cpu, SensorType.Load, "CPU Total"))),
            ["power"] = R1(V(Pick(cpu, SensorType.Power, "CPU Package", "Package"))),
            ["clock"] = clocks.Count > 0 ? (object)Math.Round(clocks.Average() / 1000.0, 2) : null,
        };

        // ---- carte graphique
        var vramUsed = V(Pick(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"));
        var vramTotal = V(Pick(gpu, SensorType.SmallData, "GPU Memory Total"));
        result["gpu"] = new Dictionary<string, object>
        {
            ["name"] = gpu?.Name ?? "",
            ["temp"] = R1(V(Pick(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot"))),
            ["load"] = R1(V(Pick(gpu, SensorType.Load, "GPU Core", "D3D 3D"))),
            ["power"] = R1(V(Pick(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power"))),
            ["clock"] = R1(V(Pick(gpu, SensorType.Clock, "GPU Core"))),
            ["vram_used"] = vramUsed.HasValue ? (object)Math.Round(vramUsed.Value / 1024.0, 2) : null,
            ["vram_total"] = vramTotal.HasValue ? (object)Math.Round(vramTotal.Value / 1024.0, 1) : null,
        };

        // ---- mémoire
        var used = V(Pick(ram, SensorType.Data, "Memory Used", "Used Memory"));
        var free = V(Pick(ram, SensorType.Data, "Memory Available", "Available Memory"));
        result["ram"] = new Dictionary<string, object>
        {
            ["load"] = R1(V(Pick(ram, SensorType.Load, "Memory"))),
            ["used"] = R1(used),
            ["total"] = used.HasValue && free.HasValue ? (object)Math.Round(used.Value + free.Value, 1) : null,
        };

        // ---- réseau : la carte Wi-Fi si on la connaît, sinon celle qui transfère le plus
        IHardware nic = null;
        double up = 0, down = 0;
        double Up(IHardware h) => V(Pick(h, SensorType.Throughput, "Upload Speed")) ?? 0;
        double Down(IHardware h) => V(Pick(h, SensorType.Throughput, "Download Speed")) ?? 0;
        if (nics.Count > 0)
        {
            nic = nics.FirstOrDefault(n => wifiAdapterName != null && n.Name == wifiAdapterName && Up(n) + Down(n) > 0)
                  ?? nics.OrderByDescending(n => Up(n) + Down(n)).First();
            up = Up(nic);
            down = Down(nic);
        }
        result["net"] = new Dictionary<string, object>
        {
            ["nic"] = nic?.Name,
            ["up"] = Math.Round(up),
            ["down"] = Math.Round(down),
        };

        // ---- disques
        // santé : usure (NVMe « Percentage Used », SATA « Remaining Life »), données écrites, place utilisée
        result["disks"] = disks.Select(d =>
            {
                var t = d.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value.HasValue);
                var used = V(Pick(d, SensorType.Level, "Percentage Used"));
                var remaining = V(Pick(d, SensorType.Level, "Remaining Life"));
                var written = V(Pick(d, SensorType.Data, "Data Written", "Total Bytes Written", "Host Writes"));
                var space = V(Pick(d, SensorType.Load, "Used Space"));
                var spare = V(Pick(d, SensorType.Level, "Available Spare"));
                return new Dictionary<string, object>
                {
                    ["name"] = d.Name,
                    ["temp"] = t != null ? (object)(int)Math.Round(t.Value.Value) : null,
                    ["life"] = remaining.HasValue ? (object)(int)Math.Round(remaining.Value) : used.HasValue ? (object)(int)Math.Round(100 - Math.Min(100, used.Value)) : null,
                    ["written"] = written.HasValue ? (object)Math.Round(written.Value / 1024.0, 1) : null, // To
                    ["used"] = space.HasValue ? (object)(int)Math.Round(space.Value) : null,
                    ["spare"] = spare.HasValue ? (object)(int)Math.Round(spare.Value) : null,
                };
            })
            .Where(x => x["temp"] != null || x["life"] != null)
            .ToList();

        return result;
    }

    public void Dispose()
    {
        try { computer?.Close(); } catch { }
    }
}
