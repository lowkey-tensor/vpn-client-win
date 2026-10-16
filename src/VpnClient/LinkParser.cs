using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace VpnClient;

/// <summary>Разбор vless:// и hysteria2:// ссылок в outbound для sing-box.</summary>
public static class LinkParser
{
    /// <summary>Xray != null — узел обслуживает Xray-core, а Outbound это socks к нему.</summary>
    public record Parsed(string Name, string Server, JsonObject Outbound, JsonObject? Xray = null);

    public static Parsed Parse(string link)
    {
        link = link.Trim();
        if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(link);
        if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(link);
        throw new FormatException("Поддерживаются только vless:// и hysteria2:// (hy2://)");
    }

    public static string Protocol(string link) =>
        link.StartsWith("vless", StringComparison.OrdinalIgnoreCase) ? "VLESS" : "Hysteria2";

    private static Parsed ParseVless(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string name = Name(u, u.Host);
        string security = (q["security"] ?? "none").ToLowerInvariant();

        // VLESS Encryption (mlkem…) и XHTTP sing-box не умеет — отдаём такие узлы Xray-core
        string enc = q["encryption"] ?? "none";
        string net = (q["type"] ?? "tcp").ToLowerInvariant();
        if (enc != "none" || net == "xhttp")
            return new Parsed(name, u.Host.Trim('[', ']'), ConfigBuilder.XraySocksOutbound(), BuildXray(u, q, security, net, enc));

        var o = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = u.Host.Trim('[', ']'),
            ["server_port"] = u.Port,
            ["uuid"] = Uri.UnescapeDataString(u.UserInfo),
            ["packet_encoding"] = "xudp",
        };
        if (!string.IsNullOrEmpty(q["flow"])) o["flow"] = q["flow"];

        if (security is "tls" or "reality")
        {
            var tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = q["sni"] ?? u.Host,
            };
            if (q["allowInsecure"] == "1") tls["insecure"] = true;
            if (!string.IsNullOrEmpty(q["alpn"]))
                tls["alpn"] = new JsonArray(q["alpn"]!.Split(',').Select(a => (JsonNode)a).ToArray());
            string fp = q["fp"] ?? (security == "reality" ? "chrome" : "");
            if (fp != "") tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
            if (security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = q["pbk"] ?? throw new FormatException("В ссылке нет pbk (public key Reality)"),
                    ["short_id"] = q["sid"] ?? "",
                };
            }
            o["tls"] = tls;
        }

        switch ((q["type"] ?? "tcp").ToLowerInvariant())
        {
            case "tcp":
                break;
            case "ws":
                var ws = new JsonObject { ["type"] = "ws", ["path"] = q["path"] ?? "/" };
                if (!string.IsNullOrEmpty(q["host"])) ws["headers"] = new JsonObject { ["Host"] = q["host"] };
                o["transport"] = ws;
                break;
            case "grpc":
                o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = q["serviceName"] ?? "" };
                break;
            case var t:
                throw new NotSupportedException($"Транспорт '{t}' не поддерживается ядром sing-box");
        }
        return new Parsed(name, u.Host.Trim('[', ']'), o);
    }

    private static JsonObject BuildXray(Uri u, System.Collections.Specialized.NameValueCollection q,
        string security, string net, string enc)
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
                ["allowInsecure"] = q["allowInsecure"] == "1",
            };

        switch (net)
        {
            case "ws":
                ss["wsSettings"] = new JsonObject { ["path"] = q["path"] ?? "/", ["host"] = q["host"] ?? "" };
                break;
            case "grpc":
                ss["grpcSettings"] = new JsonObject { ["serviceName"] = q["serviceName"] ?? "" };
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
                    ["address"] = u.Host.Trim('[', ']'),
                    ["port"] = u.Port,
                    ["users"] = new JsonArray(user),
                }),
            },
            ["streamSettings"] = ss,
        };
    }

    private static Parsed ParseHysteria2(string link)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string name = Name(u, u.Host);

        var o = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = u.Host.Trim('[', ']'),
            ["server_port"] = u.Port,
            ["password"] = Uri.UnescapeDataString(u.UserInfo),
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = q["sni"] ?? u.Host,
                ["insecure"] = q["insecure"] == "1",
                ["alpn"] = new JsonArray("h3"),
            },
        };
        if (!string.IsNullOrEmpty(q["obfs"]))
            o["obfs"] = new JsonObject { ["type"] = q["obfs"], ["password"] = q["obfs-password"] ?? "" };
        if (!string.IsNullOrEmpty(q["mport"]))
            o["server_ports"] = new JsonArray(q["mport"]!.Split(',').Select(p => (JsonNode)p.Replace('-', ':')).ToArray());
        return new Parsed(name, u.Host.Trim('[', ']'), o);
    }

    private static string Name(Uri u, string fallback)
    {
        var f = u.Fragment.TrimStart('#');
        return string.IsNullOrWhiteSpace(f) ? fallback : Uri.UnescapeDataString(f);
    }

    /// <summary>Содержимое подписки: base64 или обычный текст, по ссылке на строку.</summary>
    public static IEnumerable<string> SplitSubscription(string body)
    {
        body = body.Trim();
        if (!body.Contains("://"))
        {
            try
            {
                string b = body.Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
                b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                body = Encoding.UTF8.GetString(Convert.FromBase64String(b));
            }
            catch (FormatException) { }
        }
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(l => l.Contains("://"));
    }
}

