using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    // ---------- Wi-Fi sans la localisation ----------
    // Depuis Windows 11 24H2, « netsh wlan » et la lecture de la connexion en cours (SSID, BSSID) passent par la
    // localisation : refusées si elle est coupée, et notées dans Paramètres > Localisation toutes les 5 s.
    // On lit donc seulement ce que Windows laisse libre : état de la carte, puissance du signal (RSSI), canal,
    // vitesse du lien (IP Helper) et le nom du réseau (gestionnaire de réseaux, comme la barre des tâches).

    const uint WLAN_RSSI = 0x10000102, WLAN_CHANNEL = 8, STATE_CONNECTED = 1;
    static IntPtr wlan = IntPtr.Zero;
    static bool wlanMissing;
    static Guid ssidFor;
    static string ssidCache;
    static long ssidAt;

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, uint opCode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("iphlpapi.dll")] static extern int ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll")] static extern int GetIfEntry2(IntPtr row);

    public static Dictionary<string, object> Wifi()
    {
        if (wlanMissing) return null;
        try
        {
            if (wlan == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out _, out wlan) != 0)
            {
                wlan = IntPtr.Zero;
                return null; // service « Configuration automatique WLAN » arrêté : on réessaie au prochain tour
            }
            var (guid, ok) = ConnectedInterface();
            if (!ok) return null;

            int? rssi = QueryInt(guid, WLAN_RSSI);
            int? channel = QueryInt(guid, WLAN_CHANNEL);
            if (channel is <= 0 or > 233) channel = null;
            var (alias, rx, tx) = Link(guid);

            return new Dictionary<string, object>
            {
                ["name"] = alias,
                ["ssid"] = Ssid(guid),
                // même échelle que Windows : -100 dBm = 0 %, -50 dBm et plus = 100 %
                ["signal"] = rssi.HasValue ? (object)Math.Clamp(2 * (rssi.Value + 100), 0, 100) : null,
                ["rssi"] = rssi,
                ["band"] = channel == null ? null : channel <= 14 ? "2,4 GHz" : "5 GHz",
                ["channel"] = channel?.ToString(CultureInfo.InvariantCulture),
                ["rx"] = rx,
                ["tx"] = tx,
            };
        }
        catch (DllNotFoundException)
        {
            wlanMissing = true; // pas de Wi-Fi sur ce PC
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Première carte Wi-Fi connectée (état seulement : libre d'accès).</summary>
    static (Guid, bool) ConnectedInterface()
    {
        int err = WlanEnumInterfaces(wlan, IntPtr.Zero, out var list);
        if (err == 6) // poignée devenue invalide (service redémarré)
        {
            WlanCloseHandle(wlan, IntPtr.Zero);
            wlan = IntPtr.Zero;
            return (Guid.Empty, false);
        }
        if (err != 0 || list == IntPtr.Zero) return (Guid.Empty, false);
        try
        {
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
            {
                var item = list + 8 + i * 532; // GUID (16) + description (256 caractères) + état (4)
                if ((uint)Marshal.ReadInt32(item + 528) != STATE_CONNECTED) continue;
                return (Marshal.PtrToStructure<Guid>(item), true);
            }
            return (Guid.Empty, false);
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    static int? QueryInt(Guid guid, uint opCode)
    {
        if (WlanQueryInterface(wlan, ref guid, opCode, IntPtr.Zero, out uint size, out var data, IntPtr.Zero) != 0 || data == IntPtr.Zero) return null;
        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>Nom de la carte (« Wi-Fi ») et vitesse du lien en Mb/s, via MIB_IF_ROW2.</summary>
    static (string, double?, double?) Link(Guid guid)
    {
        string alias = null;
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == guid);
            alias = ni?.Name;
            double? fallback = ni != null && ni.Speed > 0 ? Math.Round(ni.Speed / 1e6) : null;

            if (ConvertInterfaceGuidToLuid(ref guid, out ulong luid) != 0) return (alias, fallback, null);
            const int ROW = 1352;
            var row = Marshal.AllocHGlobal(ROW);
            try
            {
                for (int i = 0; i < ROW; i += 8) Marshal.WriteInt64(row, i, 0);
                Marshal.WriteInt64(row, 0, (long)luid);
                if (GetIfEntry2(row) != 0) return (alias, fallback, null);
                string rowAlias = Marshal.PtrToStringUni(row + 28);
                double tx = Marshal.ReadInt64(row, 1192) / 1e6, rx = Marshal.ReadInt64(row, 1200) / 1e6;
                // contrôle de la structure : le nom lu doit être celui de la carte, et les vitesses plausibles
                bool sane = (alias == null || rowAlias == alias) && rx >= 1.0 && rx <= 100_000.0 && tx >= 1.0 && tx <= 100_000.0;
                if (alias == null && sane) alias = rowAlias;
                if (!sane) return (alias, fallback, null);
                return (alias, Math.Round(rx), Math.Round(tx));
            }
            finally
            {
                Marshal.FreeHGlobal(row);
            }
        }
        catch
        {
            return (alias, null, null);
        }
    }

    /// <summary>Nom du réseau connecté sur cette carte (gestionnaire de réseaux Windows), relu toutes les 30 s.</summary>
    static string Ssid(Guid adapter)
    {
        if (adapter == ssidFor && Environment.TickCount64 - ssidAt < 30_000) return ssidCache;
        ssidFor = adapter;
        ssidAt = Environment.TickCount64;
        ssidCache = null;
        object mgr = null;
        try
        {
            mgr = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
            var en = ((INetworkListManager)mgr).GetNetworkConnections();
            try
            {
                while (en.Next(1, out var conn, out uint got) == 0 && got == 1 && conn != null)
                {
                    try
                    {
                        if (conn.GetAdapterId() != adapter) continue;
                        var net = conn.GetNetwork();
                        try { ssidCache = net.GetName(); }
                        finally { Marshal.ReleaseComObject(net); }
                        break;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(conn);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(en);
            }
        }
        catch
        {
            ssidCache = null; // on affichera « Wi-Fi », nouvel essai dans 30 s
        }
        finally
        {
            if (mgr != null) Marshal.ReleaseComObject(mgr);
        }
        return ssidCache;
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection item, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkConnection
    {
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
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
