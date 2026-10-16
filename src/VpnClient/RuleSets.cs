using System.IO;
using System.Net;
using System.Net.Http;

namespace VpnClient;

/// <summary>Список рекламных доменов (geosite category-ads-all из официального репозитория SagerNet), кэш на 7 дней.</summary>
public static class RuleSets
{
    private const string AdsUrl = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-category-ads-all.srs";
    public static string AdsPath => Path.Combine(Store.Dir, "rules", "ads.srs");

    public static bool HasAds => File.Exists(AdsPath) && new FileInfo(AdsPath).Length > 1000;
    public static bool AdsStale => !HasAds || DateTime.Now - File.GetLastWriteTime(AdsPath) > TimeSpan.FromDays(7);

    /// <summary>Скачать/обновить список. proxy — адрес локального прокси, если интернет сейчас идёт через него.</summary>
    public static async Task<bool> UpdateAdsAsync(string? proxy = null)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                UseProxy = proxy != null,
                Proxy = proxy != null ? new WebProxy(proxy) : null,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await http.GetByteArrayAsync(AdsUrl);
            if (bytes.Length < 1000) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(AdsPath)!);
            string tmp = AdsPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, AdsPath, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
