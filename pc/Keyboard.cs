// Éclairage ASUS Aura (clavier, bandeau, logo) par USB HID.
// Messages repris de G-Helper (https://github.com/seerge/g-helper), licence GNU GPL v3.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace PCMonitor;

public sealed class KeyboardSettings
{
    /// <summary>0 éteint, 1 faible, 2 moyen, 3 fort.</summary>
    public int Brightness { get; set; } = 3;
    /// <summary>0 statique, 1 respiration, 2 cycle de couleurs, 3 arc-en-ciel, 10 stroboscope.</summary>
    public int Mode { get; set; } = 0;
    public string Color { get; set; } = "#FF2A2A";
    public string Color2 { get; set; } = "#000000";
    /// <summary>0 lente, 1 normale, 2 rapide.</summary>
    public int Speed { get; set; } = 1;
    public bool Keys { get; set; } = true;
    public bool Bar { get; set; } = true;
    public bool Logo { get; set; } = true;
    /// <summary>Couleurs des 4 zones du clavier (de gauche à droite), ou null pour une seule couleur.</summary>
    public List<string> Zones { get; set; }
    /// <summary>
    /// Façon d'envoyer les messages au clavier : 1 = comme G-Helper (rapports de sortie), 2 = comme rogauracore
    /// et OpenRGB pour les ROG 2018-2019 (rapports « feature »), 3 = comme 2 mais uniquement zone par zone.
    /// </summary>
    public int Method { get; set; } = 2;
}

public static class Keyboard
{
    const int AsusVendor = 0x0B05;
    const byte InputId = 0x5A;
    const byte AuraId = 0x5D;

    static readonly int[] Pids =
    {
        0x1a30, 0x1854, 0x1869, 0x1866, 0x19b6, 0x1822, 0x1837, 0x184a, 0x183d, 0x8502, 0x1807, 0x17e0,
        0x1abe, 0x1b4c, 0x1b6e, 0x1b2c, 0x8854, 0x1CE7, 0x1ce6, 0x1bf2, 0x1bf3, 0x1cd7, 0x1cd8, 0x4b42,
    };

    static readonly object Gate = new object();
    static bool? supported;
    static bool initialized;

    public static readonly int[] Modes = { 0, 1, 2, 3, 10 };

    static List<HidDevice> Find(byte reportId)
    {
        var list = new List<HidDevice>();
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(AsusVendor))
            {
                try
                {
                    if (!Pids.Contains(d.ProductID) || d.GetMaxFeatureReportLength() <= 0) continue;
                    if (d.GetReportDescriptor().TryGetReport(ReportType.Feature, reportId, out _)) list.Add(d);
                }
                catch
                {
                    // périphérique non lisible : on l'ignore
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Recherche du clavier ASUS", e);
        }
        return list;
    }

    public static bool Supported
    {
        get
        {
            if (supported == null)
            {
                supported = Find(AuraId).Count > 0 || Find(InputId).Count > 0;
                Log.Info("Éclairage ASUS Aura : " + (supported.Value ? "trouvé" : "introuvable"));
            }
            return supported.Value;
        }
    }

    static void WriteInput(byte[] data)
    {
        foreach (var d in Find(InputId))
        {
            try
            {
                using var stream = d.Open();
                var payload = new byte[d.GetMaxFeatureReportLength()];
                Array.Copy(data, payload, Math.Min(data.Length, payload.Length));
                stream.SetFeature(payload);
            }
            catch (Exception e)
            {
                Log.Error($"Clavier (entrée {d.ProductID:X})", e);
            }
        }
    }

    /// <summary>Identifiant USB du clavier trouvé (pour le journal et le téléphone).</summary>
    public static string DeviceId { get; private set; }

    static void WriteAura(bool feature, params byte[][] messages)
    {
        foreach (var d in Find(AuraId))
        {
            try
            {
                DeviceId = $"{d.ProductID:X4}";
                using var stream = d.Open();
                int len = feature ? d.GetMaxFeatureReportLength() : 0;
                foreach (var m in messages)
                {
                    if (feature)
                    {
                        var payload = new byte[Math.Max(len, m.Length)];
                        Array.Copy(m, payload, m.Length);
                        stream.SetFeature(payload);
                    }
                    else stream.Write(m);
                }
                Log.Info($"Aura {d.ProductID:X4} ({(feature ? "feature" : "sortie")}) : " +
                         string.Join(" | ", messages.Select(m => BitConverter.ToString(m, 0, Math.Min(m.Length, 13)))));
            }
            catch (Exception e)
            {
                Log.Error($"Clavier (Aura {d.ProductID:X}, {(feature ? "feature" : "sortie")})", e);
            }
        }
    }

    static int initializedMethod;

    static void Init(int method)
    {
        if (initialized && initializedMethod == method) return;
        initialized = true;
        initializedMethod = method;
        var hello = Encoding.ASCII.GetBytes("ASUS Tech.Inc.");
        if (method == 1)
            WriteAura(false, new byte[] { AuraId, 0xB9 }, new[] { AuraId }.Concat(hello).ToArray());
        else // séquence de réveil de rogauracore pour les claviers Aura Core 2018-2019
            WriteAura(true, new byte[] { AuraId, 0xB9 }, new[] { AuraId }.Concat(hello).ToArray(), new byte[] { AuraId, 0x05, 0x20, 0x31, 0x00, 0x08 });
        WriteInput(new[] { InputId }.Concat(hello).ToArray());
    }

    static (byte R, byte G, byte B) Rgb(string hex)
    {
        try
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 6)
                return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch { }
        return (255, 42, 42);
    }

    static byte Speed(KeyboardSettings s) => s.Speed switch { 0 => 0xE1, 2 => 0xF5, _ => 0xEB };

    static byte[] EffectMessage(KeyboardSettings s, byte zone, string color)
    {
        var (r, g, b) = Rgb(color);
        // Certains claviers prennent la 2e couleur pour l'effet fixe : on y remet la même (comme G-Helper en mode direct).
        var (r2, g2, b2) = Rgb(color);
        var msg = new byte[17];
        msg[0] = AuraId;
        msg[1] = 0xB3;
        msg[2] = zone;
        msg[3] = (byte)s.Mode;
        msg[4] = r;
        msg[5] = g;
        msg[6] = b;
        msg[7] = s.Method == 3 && s.Mode == 0 ? (byte)0x00 : Speed(s);
        msg[8] = 0x00; // direction
        msg[9] = (r == 0 && g == 0 && b == 0) ? (byte)0xFF : (s.Mode == 1 ? (byte)0x01 : (byte)0x00);
        msg[10] = r2;
        msg[11] = g2;
        msg[12] = b2;
        return msg;
    }

    /// <summary>
    /// Messages d'effet. Les claviers 4 zones (ROG 2018-2019, comme le Scar II) gardent une couleur par zone pour
    /// les effets fixe, respiration et stroboscope : la zone 0 seule ne suffit pas et le clavier reste noir.
    /// On envoie donc la zone 0 puis les zones 1 à 4.
    /// </summary>
    static List<byte[]> EffectMessages(KeyboardSettings s)
    {
        bool usesColor = s.Mode == 0 || s.Mode == 1 || s.Mode == 10;
        var list = new List<byte[]>();
        if (!(usesColor && s.Method == 3)) list.Add(EffectMessage(s, 0, s.Zones != null && s.Zones.Count == 4 ? s.Zones[0] : s.Color));
        if (usesColor)
            for (byte z = 1; z <= 4; z++)
                list.Add(EffectMessage(s, z, s.Zones != null && s.Zones.Count == 4 ? s.Zones[z - 1] : s.Color));
        return list;
    }

    static byte[] PowerMessage(KeyboardSettings s)
    {
        // octets : clavier (+ logo), bandeau, capot, arrière — allumé au démarrage, en marche, en veille, à l'arrêt
        byte keyb = 0, bar = 0, lid = 0;
        if (s.Logo) keyb |= 0b0101_0101;            // logo : démarrage, marche, veille, arrêt
        if (s.Keys) keyb |= 0b1010_1010;            // clavier : démarrage, marche, veille, arrêt
        if (s.Bar) bar = 0b0001_1111;
        if (s.Logo) lid = 0xFF;
        return new byte[] { AuraId, 0xBD, 0x01, keyb, bar, lid, 0x00, 0xFF };
    }

    /// <summary>Applique tous les réglages d'éclairage.</summary>
    public static (bool Ok, string Message) Apply(KeyboardSettings s)
    {
        if (!Supported) return (false, "Éclairage ASUS introuvable sur ce PC");
        lock (Gate)
        {
            if (s.Method < 1 || s.Method > 3) s.Method = 2;
            bool feature = s.Method != 1;
            Init(s.Method);
            if (!Modes.Contains(s.Mode)) s.Mode = 0;
            s.Brightness = Math.Clamp(s.Brightness, 0, 3);
            s.Speed = Math.Clamp(s.Speed, 0, 2);
            if (s.Zones != null && (s.Zones.Count != 4 || s.Zones.Any(z => z == null))) s.Zones = null;
            WriteAura(feature, PowerMessage(s));
            var msgs = EffectMessages(s);
            msgs.Add(new byte[] { AuraId, 0xB5, 0, 0, 0 });
            msgs.Add(new byte[] { AuraId, 0xB4 });
            WriteAura(feature, msgs.ToArray());
            WriteInput(new byte[] { InputId, 0xBA, 0xC5, 0xC4, (byte)s.Brightness });
        }
        return (true, "Éclairage mis à jour");
    }
}
