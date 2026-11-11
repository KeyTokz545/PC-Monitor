using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PCMonitor;

/// <summary>Connexion : latence, infos Wi-Fi, adresse IP et adresses MAC.</summary>
public static class NetInfo
{
    /// <summary>Temps d'ouverture d'une connexion TCP (équivalent d'un ping, sans droits spéciaux).</summary>
    public static int? Latency(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(1500) || !client.Connected) return null;
            return (int)Math.Round(sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, object> Wifi()
    {
        var (_, output) = Win.Run("netsh", "wlan show interfaces", 5000);
        return ParseWifi(Win.Decode(output));
    }

    static readonly Regex KeyValue = new(@"^\s*([^:]+?)\s*:\s*(.*?)\s*$");
    static readonly Regex Number = new(@"-?\d+(?:[.,]\d+)?");

    static double? Num(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Number.Match(s);
        if (!m.Success) return null;
        return double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    /// <summary>Lit « netsh wlan show interfaces », que Windows soit en français ou en anglais.</summary>
    public static Dictionary<string, object> ParseWifi(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return null;
        txt = txt.Replace(' ', ' ').Replace(' ', ' ').Replace("﻿", "");
        var low = txt.ToLowerInvariant();
        if (!low.Contains("ssid"))
        {
            if (low.Contains("locali") || low.Contains("location"))
                return new Dictionary<string, object> { ["error"] = "Windows cache le Wi-Fi : active Paramètres > Confidentialité > Localisation" };
            return null;
        }

        var blocks = new List<Dictionary<string, string>>();
        var cur = new Dictionary<string, string>();
        foreach (var raw in txt.Split('\n'))
        {
            var m = KeyValue.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var k = m.Groups[1].Value.Trim().ToLowerInvariant();
            var v = m.Groups[2].Value.Trim();
            if ((k == "nom" || k == "name") && cur.Count > 0)
            {
                blocks.Add(cur);
                cur = new Dictionary<string, string>();
            }
            if (!cur.ContainsKey(k)) cur[k] = v;
        }
        if (cur.Count > 0) blocks.Add(cur);

        var blk = blocks.FirstOrDefault(b => b.ContainsKey("ssid")) ?? blocks.FirstOrDefault() ?? new Dictionary<string, string>();

        string Get(string[] exact, string[] fragments)
        {
            foreach (var e in exact)
                if (blk.TryGetValue(e, out var v)) return v;
            foreach (var kv in blk)
                if (fragments.Any(f => kv.Key.Contains(f))) return kv.Value;
            return null;
        }

        var signal = Num(Get(new[] { "signal" }, new[] { "signal" }));
        if (signal == null)
        {
            var m = Regex.Match(txt, @"signal[^:\n]*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) signal = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var band = Get(new[] { "bande", "band" }, Array.Empty<string>());

        return new Dictionary<string, object>
        {
            ["name"] = blk.GetValueOrDefault("nom") ?? blk.GetValueOrDefault("name"),
            ["ssid"] = blk.GetValueOrDefault("ssid"),
            ["signal"] = signal.HasValue ? (object)(int)signal.Value : null,
            ["band"] = band == null ? null : Regex.Replace(band, @"\s+", " "),
            ["channel"] = Get(new[] { "canal", "channel" }, Array.Empty<string>()),
            ["radio"] = Get(new[] { "type de radio", "radio type" }, Array.Empty<string>()),
            ["rx"] = Num(Get(Array.Empty<string>(), new[] { "ception", "receive rate" })),
            ["tx"] = Num(Get(Array.Empty<string>(), new[] { "transmission", "transmit rate" })),
        };
    }

    /// <summary>Adresse IP du PC sur le réseau local.</summary>
    public static string LocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("10.255.255.255", 1); // aucun paquet n'est envoyé
            return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    static readonly Regex VirtualNic = new("virtual|hyper-v|vpn|vmware|virtualbox|tap-|wintun|bluetooth|loopback|wan miniport", RegexOptions.IgnoreCase);

    /// <summary>Adresses MAC des cartes réseau physiques (Ethernet d'abord), pour le réveil à distance.</summary>
    public static List<string> Macs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                            && !VirtualNic.IsMatch(n.Description) && !VirtualNic.IsMatch(n.Name))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .Select(n => n.GetPhysicalAddress().ToString())
                .Where(m => m.Length == 12 && m != "000000000000")
                .Distinct()
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
