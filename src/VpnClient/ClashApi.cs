using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace VpnClient;

/// <summary>Клиент встроенного в sing-box Clash API: скорость, трафик, задержка через туннель.</summary>
public sealed class ClashApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Secret { get; }

    /// <summary>up, down — байт в секунду.</summary>
    public event Action<long, long>? Traffic;

    public ClashApi()
    {
        Port = FreePort();
        Secret = Guid.NewGuid().ToString("N");
        // UseProxy=false: в режиме системного прокси не ходить к самому себе через прокси
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void StartTraffic()
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var s = await _http.GetStreamAsync("traffic", token);
                    using var r = new StreamReader(s);
                    while (await r.ReadLineAsync(token) is { } line)
                    {
                        var j = JsonNode.Parse(line);
                        Traffic?.Invoke((long?)j?["up"] ?? 0, (long?)j?["down"] ?? 0);
                    }
                }
                catch { }
                try { await Task.Delay(1000, token); } catch { }
            }
        }, token);
    }

    public async Task<(long up, long down)?> TotalsAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(3000);
            var j = JsonNode.Parse(await _http.GetStringAsync("connections", cts.Token));
            return ((long?)j?["uploadTotal"] ?? 0, (long?)j?["downloadTotal"] ?? 0);
        }
        catch { return null; }
    }

    /// <summary>Задержка запроса через туннель (мс) или null, если связи нет.</summary>
    public async Task<int?> DelayAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(9000);
            string url = Uri.EscapeDataString("https://www.gstatic.com/generate_204");
            var j = JsonNode.Parse(await _http.GetStringAsync($"proxies/proxy/delay?timeout=7000&url={url}", cts.Token));
            return (int?)j?["delay"];
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
