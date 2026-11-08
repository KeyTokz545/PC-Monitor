using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PCMonitor;

/// <summary>
/// Répond aux téléphones qui cherchent le PC sur le Wi-Fi (message « PCMONITOR? » en UDP).
/// La clé n'est jamais envoyée : le téléphone doit taper le code affiché sur le PC.
/// </summary>
public sealed class Discovery
{
    readonly int port;
    readonly Func<object> hello;
    UdpClient udp;

    public Discovery(int port, Func<object> hello)
    {
        this.port = port;
        this.hello = hello;
    }

    public void Start()
    {
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)) { EnableBroadcast = true };
            Task.Run(Loop);
            Log.Info($"Découverte UDP sur le port {port}");
        }
        catch (Exception e)
        {
            Log.Error("Découverte UDP", e);
        }
    }

    async Task Loop()
    {
        while (udp != null)
        {
            try
            {
                var r = await udp.ReceiveAsync();
                var msg = Encoding.UTF8.GetString(r.Buffer).Trim();
                if (!msg.StartsWith("PCMONITOR?")) continue;
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello(), Collector.Json));
                await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error("Découverte UDP", e);
                await Task.Delay(1000);
            }
        }
    }

    public void Stop()
    {
        try { udp?.Close(); } catch { }
        udp = null;
    }
}
