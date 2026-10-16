using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VpnClient;

/// <summary>Задержка до сервера: ICMP-пинг, если сервер на него не отвечает — время TCP-соединения.</summary>
public static class Pinger
{
    public static async Task<int?> MeasureAsync(string host, int port)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 2000);
            if (r.Status == IPStatus.Success) return (int)r.RoundtripTime;
        }
        catch { }

        try
        {
            using var c = new TcpClient();
            var sw = Stopwatch.StartNew();
            var t = c.ConnectAsync(host, port);
            if (await Task.WhenAny(t, Task.Delay(2500)) == t && c.Connected) return (int)sw.ElapsedMilliseconds;
        }
        catch { }
        return null;
    }
}
