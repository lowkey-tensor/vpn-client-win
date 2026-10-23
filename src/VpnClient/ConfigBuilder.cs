using System.Text.Json;
using System.Text.Json.Nodes;

namespace VpnClient;

public enum Mode { SystemProxy, Tun }

public static class ConfigBuilder
{
    public const int ProxyPort = 2080;
    public const int XrayPort = 10808;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>sing-box -> socks -> Xray-core (для узлов, которые sing-box не тянет).</summary>
    public static JsonObject XraySocksOutbound() => new()
    {
        ["type"] = "socks",
        ["tag"] = "proxy",
        ["server"] = "127.0.0.1",
        ["server_port"] = XrayPort,
        ["version"] = "5",
    };

    public static string BuildXray(JsonObject outbound) => new JsonObject
    {
        ["log"] = new JsonObject { ["loglevel"] = "warning", ["access"] = "none" },
        ["inbounds"] = new JsonArray(new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = XrayPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["udp"] = true },
        }),
        ["outbounds"] = new JsonArray(outbound.DeepClone()),
    }.ToJsonString(Pretty);

    /// <summary>Российские сайты — напрямую (Ozon, банки, Госуслуги не видят VPN).</summary>
    public static readonly string[] RuDomains =
    [
        "ru", "su", "xn--p1ai" /* .рф */, "xn--d1acj3b" /* .дети */, "moscow", "tatar",
        // российские сервисы на не-.ru доменах
        "yandex.net", "yandex.com", "yastatic.net", "yandexcloud.net", "ya.cc",
        "vk.com", "vk.me", "vkuser.net", "userapi.com", "vk-cdn.net", "vkuseraudio.net", "vkvideo.ru",
        "mycdn.me", "ok.me", "mail.ru", "mradx.net", "imgsmail.ru",
        "ozonusercontent.com", "ozone.ru", "wbbasket.ru", "wbstatic.net", "avito.st",
        "kaspersky.com", "1c.ru", "rutube.ru", "dzen.ru",
    ];

    public static string Build(JsonObject outbound, Mode mode, BuildOptions o)
    {
        static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(d => (JsonNode)d).ToArray());
        JsonArray Ru() => Arr(RuDomains);
        bool ruDirect = o.RuDirect;
        var exclude = Arr(o.ServerIps.Select(ip => ip.Contains(':') ? ip + "/128" : ip + "/32"));

        JsonObject inbound = mode == Mode.Tun
            ? new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "mixed",
                ["route_exclude_address"] = exclude,
            }
            : new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = ProxyPort,
            };

        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject { ["type"] = "https", ["tag"] = "dns-remote", ["server"] = "1.1.1.1", ["detour"] = "proxy" },
                    // DNS Яндекса по TLS (порт 853): обычный UDP:53 в TUN режется защитой от утечек DNS (strict_route)
                    new JsonObject
                    {
                        ["type"] = "tls", ["tag"] = "dns-local", ["server"] = "77.88.8.8",
                        ["tls"] = new JsonObject { ["server_name"] = "common.dot.dns.yandex.net" },
                    }),
                ["rules"] = new JsonArray(),
                ["final"] = "dns-remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = new JsonArray(
                outbound.DeepClone(),
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(),
                ["final"] = "proxy",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-local",
            },
            // статистика и проверка связи для окна клиента
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{o.ClashPort}",
                    ["secret"] = o.ClashSecret,
                },
            },
        };

        var dnsRules = (JsonArray)cfg["dns"]!["rules"]!;
        var rules = (JsonArray)cfg["route"]!["rules"]!;
        static JsonObject Route(string key, JsonNode val, string outb) => new() { [key] = val, ["action"] = "route", ["outbound"] = outb };

        rules.Add(new JsonObject { ["action"] = "sniff" });
        rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });

        // реклама и трекеры
        if (o.AdsRuleSetPath != null)
        {
            cfg["route"]!["rule_set"] = new JsonArray(new JsonObject
            {
                ["type"] = "local", ["tag"] = "ads", ["format"] = "binary", ["path"] = o.AdsRuleSetPath,
            });
            dnsRules.Add(new JsonObject { ["rule_set"] = "ads", ["action"] = "reject" });
            rules.Add(new JsonObject { ["rule_set"] = "ads", ["action"] = "reject" });
        }

        rules.Add(Route("ip_is_private", true, "direct"));

        // свои списки сайтов: домены (с поддоменами) и IP/подсети
        string[] proxyDomains = o.ProxySites.Where(s => !RouteRules.IsIpRule(s)).ToArray();
        string[] proxyIps = o.ProxySites.Where(RouteRules.IsIpRule).Select(RouteRules.AsCidr).ToArray();
        string[] directDomains = o.DirectSites.Where(s => !RouteRules.IsIpRule(s)).ToArray();
        string[] directIps = o.DirectSites.Where(RouteRules.IsIpRule).Select(RouteRules.AsCidr).ToArray();

        // «только выбранные программы через VPN» работает лишь в TUN и лишь при непустом списке:
        // в режиме системного прокси остальные программы всё равно идут через прокси, а пустой список отправил бы весь трафик мимо VPN
        bool onlyApps = o.AppsOnlyViaVpn && mode == Mode.Tun && o.DirectApps.Length > 0;

        void ProxySitesRules(bool scopedQuicReject)
        {
            if (proxyDomains.Length > 0)
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(proxyDomains), ["action"] = "route", ["server"] = "dns-remote" });
            if (scopedQuicReject)   // QUIC в туннель не пускаем и в режиме «только выбранные программы»
            {
                if (proxyDomains.Length > 0) rules.Add(new JsonObject { ["domain_suffix"] = Arr(proxyDomains), ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
                if (proxyIps.Length > 0) rules.Add(new JsonObject { ["ip_cidr"] = Arr(proxyIps), ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
            }
            if (proxyDomains.Length > 0) rules.Add(Route("domain_suffix", Arr(proxyDomains), "proxy"));
            if (proxyIps.Length > 0) rules.Add(Route("ip_cidr", Arr(proxyIps), "proxy"));
        }
        void DirectSitesRules()
        {
            if (directDomains.Length > 0)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(directDomains), ["action"] = "route", ["server"] = "dns-local" });
                rules.Add(Route("domain_suffix", Arr(directDomains), "direct"));
            }
            if (directIps.Length > 0) rules.Add(Route("ip_cidr", Arr(directIps), "direct"));
        }
        void RuRules()
        {
            if (!ruDirect) return;
            dnsRules.Add(new JsonObject { ["domain_suffix"] = Ru(), ["action"] = "route", ["server"] = "dns-local" });
            rules.Add(Route("domain_suffix", Ru(), "direct"));
        }

        if (!onlyApps)
        {
            // программы напрямую (игры) — со всем их UDP, до блока QUIC
            if (o.DirectApps.Length > 0)
                rules.Add(Route("process_name", Arr(o.DirectApps), "direct"));

            // QUIC (UDP 443) режем для всех сайтов — браузер сразу уходит на TCP.
            // Иначе Chrome подвисает на QUIC (YouTube через туннель, Ozon и др. напрямую).
            rules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });

            // «через VPN» важнее остальных правил о сайтах
            ProxySitesRules(scopedQuicReject: false);
            DirectSitesRules();
            RuRules();
        }
        else
        {
            // всё идёт напрямую, кроме перечисленных программ и сайтов «всегда через VPN»
            ProxySitesRules(scopedQuicReject: true);
            DirectSitesRules();
            RuRules();
            rules.Add(new JsonObject { ["process_name"] = Arr(o.DirectApps), ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
            rules.Add(Route("process_name", Arr(o.DirectApps), "proxy"));
            cfg["route"]!["final"] = "direct";
        }

        return cfg.ToJsonString(Pretty);
    }
}

public record BuildOptions(
    IEnumerable<string> ServerIps,
    bool RuDirect,
    string[] DirectApps,
    string[] DirectSites,
    string[] ProxySites,
    string? AdsRuleSetPath,
    int ClashPort,
    string ClashSecret,
    bool AppsOnlyViaVpn = false);
