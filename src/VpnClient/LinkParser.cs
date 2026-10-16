using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace VpnClient;

/// <summary>
/// Разбор ссылок vless / vmess / trojan / ss / tuic / hysteria2 / hysteria / anytls / socks
/// в outbound для sing-box (а для VLESS-шифрования и XHTTP — в конфиг Xray-core).
/// </summary>
public static class LinkParser
{
    /// <summary>Xray != null — узел обслуживает Xray-core, а Outbound это socks к нему.</summary>
    public record Parsed(string Name, string Server, int Port, JsonObject Outbound, JsonObject? Xray = null);

    public const string SupportedHint = "vless, vmess, trojan, ss, tuic, hysteria2, hysteria, anytls, socks";

    public static string SchemeOf(string link)
    {
        link = link.Trim();
        int i = link.IndexOf("://", StringComparison.Ordinal);
        return i <= 0 ? "" : link[..i].ToLowerInvariant();
    }

    public static string Protocol(string link) => SchemeOf(link) switch
    {
        "vless" => "VLESS",
        "vmess" => "VMess",
        "trojan" => "Trojan",
        "ss" => "Shadowsocks",
        "tuic" => "TUIC",
        "hysteria2" or "hy2" => "Hysteria2",
        "hysteria" => "Hysteria",
        "anytls" => "AnyTLS",
        "socks" or "socks5" or "socks5h" => "SOCKS",
        _ => "?",
    };

    public static bool IsSupported(string link) => Protocol(link) != "?";

    public static Parsed Parse(string link)
    {
        link = link.Trim();
        return SchemeOf(link) switch
        {
            "vless" => ParseVless(link),
            "vmess" => ParseVmess(link),
            "trojan" => ParseTrojan(link),
            "ss" => ParseShadowsocks(link),
            "tuic" => ParseTuic(link),
            "hysteria2" or "hy2" => ParseHysteria2(link),
            "hysteria" => ParseHysteria(link),
            "anytls" => ParseAnyTls(link),
            "socks" or "socks5" or "socks5h" => ParseSocks(link),
            _ => throw new FormatException($"Неизвестный тип ссылки. Поддерживаются: {SupportedHint}"),
        };
    }

    // адрес сервера для списка и пинга (с кэшем: разбор ссылки дёшев, но вызывается часто)
    private static readonly ConcurrentDictionary<string, (string, int)?> EndpointCache = new();

    public static (string Host, int Port)? Endpoint(string link) =>
        EndpointCache.GetOrAdd(link, l =>
        {
            try { var p = Parse(l); return (p.Server, p.Port); }
            catch { return null; }
        });

    // ================= общие помощники =================

    private static string? Q(NameValueCollection q, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = q[k];
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private static bool Flag(NameValueCollection q, params string[] keys) => Q(q, keys) is "1" or "true" or "True";

    private static string First(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";

    private static string HostOf(Uri u) => u.Host.Trim('[', ']');

    private static int PortOf(Uri u) =>
        u.Port > 0 ? u.Port : throw new FormatException("В ссылке не указан порт");

    private static string NameOf(Uri u, string fallback)
    {
        var f = u.Fragment.TrimStart('#');
        return string.IsNullOrWhiteSpace(f) ? fallback : Uri.UnescapeDataString(f);
    }

    private static string DecodeB64(string s)
    {
        s = s.Trim().Replace("\r", "").Replace("\n", "").Replace('-', '+').Replace('_', '/').TrimEnd('=');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static JsonArray Arr(string csv) =>
        new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(a => (JsonNode)a).ToArray());

    private static JsonObject Tls(string serverName, bool insecure, string? alpn, string? fp,
        string? pbk = null, string? sid = null)
    {
        var tls = new JsonObject { ["enabled"] = true, ["server_name"] = serverName };
        if (insecure) tls["insecure"] = true;
        if (!string.IsNullOrEmpty(alpn)) tls["alpn"] = Arr(alpn);
        if (!string.IsNullOrEmpty(fp) && fp != "none") tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        if (pbk != null) tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = pbk, ["short_id"] = sid ?? "" };
        return tls;
    }

    /// <summary>Транспорт sing-box: tcp / ws / grpc / h2 / httpupgrade / quic.</summary>
    private static JsonObject? Transport(string net, string? path, string? host, string? service, bool headerHttp = false)
    {
        switch (net.ToLowerInvariant())
        {
            case "" or "tcp" or "raw":
                if (headerHttp)
                    throw new NotSupportedException("TCP с HTTP-маскировкой (header=http) не поддерживается — выберите другой транспорт");
                return null;

            case "ws" or "websocket":
            {
                var ws = new JsonObject { ["type"] = "ws" };
                string p = string.IsNullOrEmpty(path) ? "/" : path;
                // ранние данные: /path?ed=2048 -> отдельные поля sing-box
                int qi = p.IndexOf("?ed=", StringComparison.Ordinal);
                if (qi >= 0 && int.TryParse(p[(qi + 4)..].Split('&')[0], out int ed))
                {
                    ws["max_early_data"] = ed;
                    ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                    p = p[..qi];
                }
                ws["path"] = p;
                if (!string.IsNullOrEmpty(host)) ws["headers"] = new JsonObject { ["Host"] = host };
                return ws;
            }

            case "grpc" or "gun":
                return new JsonObject { ["type"] = "grpc", ["service_name"] = service ?? "" };

            case "h2" or "http":
            {
                var h = new JsonObject { ["type"] = "http", ["path"] = string.IsNullOrEmpty(path) ? "/" : path };
                if (!string.IsNullOrEmpty(host)) h["host"] = Arr(host);
                return h;
            }

            case "httpupgrade":
            {
                var h = new JsonObject { ["type"] = "httpupgrade", ["path"] = string.IsNullOrEmpty(path) ? "/" : path };
                if (!string.IsNullOrEmpty(host)) h["host"] = host;
                return h;
            }

            case "quic":
                return new JsonObject { ["type"] = "quic" };

            default:
                throw new NotSupportedException(
                    $"Транспорт «{net}» ядро sing-box не поддерживает (mKCP, XHTTP). XHTTP работает только для VLESS — через Xray.");
        }
    }

    private static void SetTransport(JsonObject outbound, JsonObject? transport)
    {
        if (transport != null) outbound["transport"] = transport;
    }

    private static string SniOf(NameValueCollection q, string fallback) => First(Q(q, "sni", "serverName", "peer"), fallback);

    private static bool InsecureOf(NameValueCollection q) => Flag(q, "allowInsecure", "allow_insecure", "insecure", "skip-cert-verify");

    // ================= VLESS =================

    private static Parsed ParseVless(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);
        string name = NameOf(u, host);
        string security = (q["security"] ?? "none").ToLowerInvariant();
        string enc = q["encryption"] ?? "none";
        string net = (q["type"] ?? "tcp").ToLowerInvariant();
        if (net == "splithttp") net = "xhttp";

        // VLESS Encryption (mlkem…) и XHTTP sing-box не умеет — отдаём такие узлы Xray-core
        if (enc != "none" || net == "xhttp")
            return new Parsed(name, host, port, ConfigBuilder.XraySocksOutbound(), BuildXray(u, q, security, net, enc));

        var o = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = Uri.UnescapeDataString(u.UserInfo),
            ["packet_encoding"] = "xudp",
        };
        if (!string.IsNullOrEmpty(q["flow"])) o["flow"] = q["flow"];

        if (security is "tls" or "reality")
        {
            bool reality = security == "reality";
            o["tls"] = Tls(SniOf(q, host), InsecureOf(q), q["alpn"],
                Q(q, "fp") ?? (reality ? "chrome" : null),
                reality ? q["pbk"] ?? throw new FormatException("В ссылке нет pbk (public key Reality)") : null,
                reality ? q["sid"] : null);
        }

        SetTransport(o, Transport(net, q["path"], q["host"], Q(q, "serviceName", "service_name"), q["headerType"] == "http"));
        return new Parsed(name, host, port, o);
    }

    private static JsonObject BuildXray(Uri u, NameValueCollection q, string security, string net, string enc)
    {
        var user = new JsonObject
        {
            ["id"] = Uri.UnescapeDataString(u.UserInfo),
            ["encryption"] = enc,
        };
        if (!string.IsNullOrEmpty(q["flow"])) user["flow"] = q["flow"];

        var ss = new JsonObject { ["network"] = net, ["security"] = security };
        if (security == "reality")
            ss["realitySettings"] = new JsonObject
            {
                ["serverName"] = q["sni"] ?? u.Host,
                ["fingerprint"] = q["fp"] ?? "chrome",
                ["publicKey"] = q["pbk"] ?? throw new FormatException("В ссылке нет pbk"),
                ["shortId"] = q["sid"] ?? "",
                ["spiderX"] = q["spx"] ?? "",
            };
        else if (security == "tls")
            ss["tlsSettings"] = new JsonObject
            {
                ["serverName"] = q["sni"] ?? u.Host,
                ["fingerprint"] = q["fp"] ?? "chrome",
                ["allowInsecure"] = InsecureOf(q),
            };

        switch (net)
        {
            case "ws":
                ss["wsSettings"] = new JsonObject { ["path"] = q["path"] ?? "/", ["host"] = q["host"] ?? "" };
                break;
            case "grpc":
                ss["grpcSettings"] = new JsonObject { ["serviceName"] = Q(q, "serviceName", "service_name") ?? "" };
                break;
            case "httpupgrade":
                ss["httpupgradeSettings"] = new JsonObject { ["path"] = q["path"] ?? "/", ["host"] = q["host"] ?? "" };
                break;
            case "xhttp":
                ss["xhttpSettings"] = new JsonObject
                {
                    ["path"] = q["path"] ?? "/",
                    ["host"] = q["host"] ?? "",
                    ["mode"] = q["mode"] ?? "auto",
                };
                break;
        }

        return new JsonObject
        {
            ["protocol"] = "vless",
            ["tag"] = "proxy",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = HostOf(u),
                    ["port"] = PortOf(u),
                    ["users"] = new JsonArray(user),
                }),
            },
            ["streamSettings"] = ss,
        };
    }

    // ================= VMess =================

    private static Parsed ParseVmess(string link)
    {
        string body = link["vmess://".Length..];
        string main = body.Split('#')[0];
        string frag = body.Contains('#') ? Uri.UnescapeDataString(body[(body.IndexOf('#') + 1)..]) : "";
        string b64 = main.Split('?')[0];

        // vmess://uuid@host:port?... (в стиле VLESS)
        if (b64.Contains('@')) return ParseVmessUri(link);

        string raw = DecodeB64(b64);
        return raw.TrimStart().StartsWith('{')
            ? ParseVmessJson(raw, frag)
            : ParseVmessShadowrocket(raw, main.Contains('?') ? main[(main.IndexOf('?') + 1)..] : "", frag);
    }

    /// <summary>Формат v2rayN / v2rayNG: vmess://base64({"v":"2","ps":..,"add":..,"port":..,"id":..,...}).</summary>
    private static Parsed ParseVmessJson(string raw, string frag)
    {
        var j = JsonNode.Parse(raw) ?? throw new FormatException("Пустая VMess-ссылка");
        string S(string k) => j[k]?.ToString() ?? "";

        string host = S("add").Trim('[', ']');
        int port = int.TryParse(S("port"), out var pp) ? pp : throw new FormatException("В VMess-ссылке нет порта");
        string name = First(S("ps"), frag, host);
        string net = First(S("net"), "tcp");

        var o = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = S("id"),
            ["security"] = First(S("scy"), "auto"),
            ["alter_id"] = int.TryParse(S("aid"), out var aid) ? aid : 0,
            ["packet_encoding"] = "xudp",
        };
        if (S("tls").Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            string sni = First(S("sni"), S("host").Split(',')[0], host);
            bool insecure = S("allowInsecure") is "1" or "true" or "True" || S("insecure") is "1" or "true";
            o["tls"] = Tls(sni, insecure, S("alpn"), S("fp"));
        }
        // для gRPC в v2rayN имя сервиса лежит в поле path
        SetTransport(o, Transport(net, S("path"), S("host"), S("path"), S("type") == "http"));
        return new Parsed(name, host, port, o);
    }

    /// <summary>Новый формат: vmess://uuid@host:port?type=ws&amp;security=tls&amp;...#имя.</summary>
    private static Parsed ParseVmessUri(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);
        string name = NameOf(u, host);
        string net = (q["type"] ?? "tcp").ToLowerInvariant();

        var o = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = Uri.UnescapeDataString(u.UserInfo),
            ["security"] = First(Q(q, "encryption", "scy"), "auto"),
            ["alter_id"] = int.TryParse(Q(q, "alterId", "aid"), out var aid) ? aid : 0,
            ["packet_encoding"] = "xudp",
        };
        string security = (q["security"] ?? "none").ToLowerInvariant();
        if (security is "tls" or "reality")
        {
            bool reality = security == "reality";
            o["tls"] = Tls(SniOf(q, host), InsecureOf(q), q["alpn"], Q(q, "fp") ?? (reality ? "chrome" : null),
                reality ? q["pbk"] ?? throw new FormatException("В ссылке нет pbk") : null, reality ? q["sid"] : null);
        }
        SetTransport(o, Transport(net, q["path"], q["host"], Q(q, "serviceName", "service_name"), q["headerType"] == "http"));
        return new Parsed(name, host, port, o);
    }

    /// <summary>Формат Shadowrocket: vmess://base64(метод:uuid@host:port)?remarks=..&amp;obfs=websocket&amp;...</summary>
    private static Parsed ParseVmessShadowrocket(string raw, string query, string frag)
    {
        int at = raw.LastIndexOf('@');
        if (at < 0) throw new FormatException("Не удалось разобрать VMess-ссылку");
        string cred = raw[..at], hp = raw[(at + 1)..];
        int c = cred.IndexOf(':');
        string method = c > 0 ? cred[..c] : "auto";
        string uuid = c > 0 ? cred[(c + 1)..] : cred;
        int pc = hp.LastIndexOf(':');
        string host = hp[..pc].Trim('[', ']');
        int port = int.Parse(hp[(pc + 1)..]);

        var q = HttpUtility.ParseQueryString(query);
        string name = First(Q(q, "remarks", "remark"), frag, host);
        string obfs = (q["obfs"] ?? "none").ToLowerInvariant();

        var o = new JsonObject
        {
            ["type"] = "vmess", ["tag"] = "proxy", ["server"] = host, ["server_port"] = port,
            ["uuid"] = uuid, ["security"] = method,
            ["alter_id"] = int.TryParse(q["alterId"], out var aid) ? aid : 0,
            ["packet_encoding"] = "xudp",
        };
        if (q["tls"] == "1") o["tls"] = Tls(First(q["peer"], q["obfsParam"], host), InsecureOf(q), null, null);
        string net = obfs switch { "none" => "tcp", "websocket" or "ws" => "ws", "http" => "http-header", _ => obfs };
        if (net == "http-header") throw new NotSupportedException("VMess с HTTP-маскировкой (obfs=http) не поддерживается");
        SetTransport(o, Transport(net, q["path"], q["obfsParam"], null));
        return new Parsed(name, host, port, o);
    }

    // ================= Trojan =================

    private static Parsed ParseTrojan(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);
        string name = NameOf(u, host);
        string security = (q["security"] ?? "tls").ToLowerInvariant();
        string net = (q["type"] ?? "tcp").ToLowerInvariant();

        var o = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["password"] = Uri.UnescapeDataString(u.UserInfo),
        };
        if (security is "tls" or "reality")
        {
            bool reality = security == "reality";
            o["tls"] = Tls(SniOf(q, host), InsecureOf(q), q["alpn"], Q(q, "fp") ?? (reality ? "chrome" : null),
                reality ? q["pbk"] ?? throw new FormatException("В ссылке нет pbk") : null, reality ? q["sid"] : null);
        }
        SetTransport(o, Transport(net, q["path"], q["host"], Q(q, "serviceName", "service_name"), q["headerType"] == "http"));
        return new Parsed(name, host, port, o);
    }

    // ================= Shadowsocks =================

    private static (string, string) SplitCred(string cred)
    {
        int c = cred.IndexOf(':');
        if (c <= 0) throw new FormatException("Некорректная ss://-ссылка: не найден метод шифрования");
        return (cred[..c], cred[(c + 1)..]);
    }

    private static (string, int) SplitHostPort(string hp)
    {
        hp = hp.TrimEnd('/');
        int c = hp.LastIndexOf(':');
        if (c <= 0) throw new FormatException("Некорректная ss://-ссылка: не найден порт");
        return (hp[..c].Trim('[', ']'), int.Parse(hp[(c + 1)..]));
    }

    private static Parsed ParseShadowsocks(string link)
    {
        string body = link["ss://".Length..];
        string name = "";
        int h = body.IndexOf('#');
        if (h >= 0) { name = Uri.UnescapeDataString(body[(h + 1)..]); body = body[..h]; }
        string query = "";
        int qm = body.IndexOf('?');
        if (qm >= 0) { query = body[(qm + 1)..]; body = body[..qm]; }
        body = body.TrimEnd('/');

        string method, password, host;
        int port;
        int at = body.LastIndexOf('@');
        if (at < 0)
        {
            // старый формат: ss://base64(метод:пароль@host:port)
            string raw = DecodeB64(Uri.UnescapeDataString(body));
            at = raw.LastIndexOf('@');
            if (at < 0) throw new FormatException("Некорректная ss://-ссылка");
            (method, password) = SplitCred(raw[..at]);
            (host, port) = SplitHostPort(raw[(at + 1)..]);
        }
        else
        {
            // SIP002: ss://base64(метод:пароль)@host:port  или  ss://метод:пароль@host:port
            string cred = Uri.UnescapeDataString(body[..at]);
            if (!cred.Contains(':')) cred = DecodeB64(cred);
            (method, password) = SplitCred(cred);
            (host, port) = SplitHostPort(body[(at + 1)..]);
        }

        var o = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password,
        };

        string? plugin = HttpUtility.ParseQueryString(query)["plugin"];
        if (!string.IsNullOrEmpty(plugin))
        {
            var parts = plugin.Split(';', 2);
            string pn = parts[0] switch { "simple-obfs" => "obfs-local", var x => x };
            if (pn is not ("obfs-local" or "v2ray-plugin"))
                throw new NotSupportedException($"Плагин Shadowsocks «{parts[0]}» не поддерживается (доступны obfs-local и v2ray-plugin)");
            o["plugin"] = pn;
            if (parts.Length > 1) o["plugin_opts"] = parts[1];
        }
        return new Parsed(First(name, host), host, port, o);
    }

    // ================= TUIC =================

    private static Parsed ParseTuic(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);

        string ui = Uri.UnescapeDataString(u.UserInfo);
        int c = ui.IndexOf(':');
        string uuid = c >= 0 ? ui[..c] : ui;
        string pass = c >= 0 ? ui[(c + 1)..] : "";

        var o = new JsonObject
        {
            ["type"] = "tuic",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["password"] = pass,
            ["congestion_control"] = First(Q(q, "congestion_control", "congestion-controller", "congestion_controller"), "bbr"),
            ["udp_relay_mode"] = First(Q(q, "udp_relay_mode", "udp-relay-mode"), "native"),
            ["tls"] = Tls(SniOf(q, host), InsecureOf(q), First(q["alpn"], "h3"), null),
        };
        return new Parsed(NameOf(u, host), host, port, o);
    }

    // ================= Hysteria2 / Hysteria =================

    private static Parsed ParseHysteria2(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);

        var o = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["password"] = Uri.UnescapeDataString(u.UserInfo),
            ["tls"] = Tls(SniOf(q, host), InsecureOf(q), "h3", null),
        };
        if (!string.IsNullOrEmpty(q["obfs"]))
            o["obfs"] = new JsonObject { ["type"] = q["obfs"], ["password"] = q["obfs-password"] ?? "" };
        if (!string.IsNullOrEmpty(q["mport"]))
            o["server_ports"] = new JsonArray(q["mport"]!.Split(',').Select(p => (JsonNode)p.Replace('-', ':')).ToArray());
        return new Parsed(NameOf(u, host), host, port, o);
    }

    /// <summary>Hysteria v1: hysteria://host:port?auth=..&amp;peer=sni&amp;insecure=1&amp;upmbps=..&amp;downmbps=..</summary>
    private static Parsed ParseHysteria(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);

        string proto = (q["protocol"] ?? "udp").ToLowerInvariant();
        if (proto != "udp")
            throw new NotSupportedException($"Hysteria v1 с протоколом «{proto}» не поддерживается (только udp)");

        static int Mbps(string? s)
        {
            var digits = new string((s ?? "").TakeWhile(ch => char.IsDigit(ch)).ToArray());
            return int.TryParse(digits, out var v) && v > 0 ? v : 100;
        }

        var o = new JsonObject
        {
            ["type"] = "hysteria",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["up_mbps"] = Mbps(Q(q, "upmbps", "up_mbps", "up")),
            ["down_mbps"] = Mbps(Q(q, "downmbps", "down_mbps", "down")),
            ["auth_str"] = Uri.UnescapeDataString(First(Q(q, "auth", "auth_str", "password"), u.UserInfo)),
            ["tls"] = Tls(SniOf(q, host), InsecureOf(q), q["alpn"], null),
        };
        string? obfs = Q(q, "obfsParam") ?? (q["obfs"] is { } ob && ob is not ("xplus" or "none") ? ob : null);
        if (obfs != null) o["obfs"] = obfs;
        return new Parsed(NameOf(u, host), host, port, o);
    }

    // ================= AnyTLS =================

    private static Parsed ParseAnyTls(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string host = HostOf(u);
        int port = PortOf(u);

        var o = new JsonObject
        {
            ["type"] = "anytls",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["password"] = Uri.UnescapeDataString(u.UserInfo),
            ["tls"] = Tls(SniOf(q, host), InsecureOf(q), q["alpn"], Q(q, "fp")),
        };
        return new Parsed(NameOf(u, host), host, port, o);
    }

    // ================= SOCKS =================

    private static Parsed ParseSocks(string link)
    {
        var u = new Uri(link);
        string host = HostOf(u);
        int port = PortOf(u);

        string ui = Uri.UnescapeDataString(u.UserInfo);
        if (ui != "" && !ui.Contains(':'))
        {
            try { ui = DecodeB64(ui); } catch (FormatException) { }
        }
        int c = ui.IndexOf(':');

        var o = new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["version"] = "5",
        };
        if (ui != "")
        {
            o["username"] = c >= 0 ? ui[..c] : ui;
            if (c >= 0) o["password"] = ui[(c + 1)..];
        }
        return new Parsed(NameOf(u, host), host, port, o);
    }

    // ================= подписки =================

    /// <summary>Содержимое подписки: base64 или обычный текст, по ссылке на строку.</summary>
    public static IEnumerable<string> SplitSubscription(string body)
    {
        body = body.Trim();
        if (!body.Contains("://"))
        {
            try { body = DecodeB64(body); }
            catch (FormatException) { }
        }
        // строки вида схема://… — кроме http(s) (это ссылки на сайты, не узлы); неподдерживаемые схемы
        // (wireguard:// и т.п.) пропускаем дальше, чтобы клиент написал в журнале, что именно не подошло
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(l => SchemeOf(l) is { Length: > 0 } s && s is not ("http" or "https"));
    }
}
