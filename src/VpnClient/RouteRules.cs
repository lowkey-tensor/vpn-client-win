using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VpnClient;

/// <summary>Как работает список программ: «мимо VPN» (по умолчанию) или «только эти — через VPN» (только TUN).</summary>
public enum AppsMode { DirectList, OnlyListedViaVpn }

/// <summary>Разбор и проверка пользовательских правил: сайты (домены, IPv4, CIDR) и программы, импорт/экспорт списка.</summary>
public static class RouteRules
{
    private static readonly Regex Ipv4Cidr = new(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:/(\d{1,2}))?$", RegexOptions.Compiled);
    private static readonly Regex Domain = new(@"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$", RegexOptions.Compiled);

    /// <summary>IP-адрес или подсеть (а не домен).</summary>
    public static bool IsIpRule(string v) => Ipv4Cidr.IsMatch(v);

    /// <summary>В формат sing-box ip_cidr: одиночный адрес → /32.</summary>
    public static string AsCidr(string v) => v.Contains('/') ? v : v + "/32";

    /// <summary>
    /// Приводит ввод пользователя к правилу: «https://www.Sber.ru/page» → «sber.ru», «1.2.3.4» → «1.2.3.4», «Россия.рф» → punycode.
    /// Возвращает null и текст ошибки, если ввод не подходит.
    /// </summary>
    public static string? NormalizeSite(string? input, out string? error)
    {
        error = null;
        string x = (input ?? "").Trim().Trim('"', '\'', '<', '>', ',', ';').ToLowerInvariant();
        if (x.Length == 0) { error = "Введите адрес сайта."; return null; }

        int i = x.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) x = x[(i + 3)..];
        int at = x.LastIndexOf('@');
        if (at >= 0) x = x[(at + 1)..];

        // IPv4 и подсети разбираем до обрезки пути: в «1.2.3.0/24» косая черта — часть правила
        string cand = x.Split('?', '#')[0].TrimEnd('/');
        var m = Ipv4Cidr.Match(cand.Contains(':') && !cand.Contains('/') ? cand.Split(':')[0] : cand);
        if (m.Success)
        {
            for (int g = 1; g <= 4; g++)
                if (int.Parse(m.Groups[g].Value) > 255) { error = "Некорректный IP-адрес."; return null; }
            if (m.Groups[5].Success && int.Parse(m.Groups[5].Value) > 32) { error = "Маска подсети должна быть от 0 до 32."; return null; }
            return m.Groups[5].Success ? $"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}.{m.Groups[4]}/{m.Groups[5]}"
                                       : $"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}.{m.Groups[4]}";
        }

        x = x.Split('/', '?', '#')[0];
        if (x.StartsWith('[') || x.Count(c => c == ':') > 1) { error = "IPv6-адреса пока не поддерживаются."; return null; }
        int colon = x.IndexOf(':');
        if (colon >= 0) x = x[..colon];                       // порт
        x = x.TrimStart('*', '.').TrimEnd('.');
        if (x.StartsWith("www.", StringComparison.Ordinal) && x.Length > 4) x = x[4..];
        if (x.Length == 0 || x == "www") { error = "Введите адрес сайта."; return null; }

        try { x = new IdnMapping().GetAscii(x); }
        catch (ArgumentException) { error = "Недопустимые символы в адресе."; return null; }
        if (x.Length > 253 || !Domain.IsMatch(x)) { error = "Адрес сайта указан неверно. Пример: sber.ru"; return null; }
        return x;
    }

    /// <summary>Имя программы: «C:\Games\Roblox\RobloxPlayerBeta.exe» → «RobloxPlayerBeta.exe».</summary>
    public static string? NormalizeApp(string? input, out string? error)
    {
        error = null;
        string x = (input ?? "").Trim().Trim('"');
        if (x.Length == 0) { error = "Введите имя программы."; return null; }
        x = System.IO.Path.GetFileName(x);
        if (x.Length == 0 || x.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { error = "Недопустимое имя программы."; return null; }
        if (!x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) x += ".exe";
        return x;
    }

    // ---- копирование и вставка всего списка ----
    public const string Header = "# VPN-клиент: правила маршрутов";

    public static string Export(State s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine("apps-mode " + (s.AppsMode == AppsMode.OnlyListedViaVpn ? "only-vpn" : "direct"));
        foreach (var d in s.ProxySites) sb.AppendLine("site-vpn " + d);
        foreach (var d in s.DirectSites) sb.AppendLine("site-direct " + d);
        foreach (var a in s.DirectApps) sb.AppendLine("app " + a);
        return sb.ToString();
    }

    public sealed record ImportResult(int Added, int Skipped, List<string> Errors);

    /// <summary>Добавляет правила из текста к состоянию (дубликаты пропускаются). Режим программ меняется только если он указан в тексте.</summary>
    public static ImportResult Import(State s, string? text)
    {
        int added = 0, skipped = 0; var errors = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            string kind = sp < 0 ? line : line[..sp];
            string val = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (kind)
            {
                case "apps-mode":
                    s.AppsMode = val == "only-vpn" ? AppsMode.OnlyListedViaVpn : AppsMode.DirectList; break;
                case "site-vpn" or "site-direct":
                {
                    var v = NormalizeSite(val, out var err);
                    if (v == null) { errors.Add($"{line} — {err}"); break; }
                    var (target, other) = kind == "site-vpn" ? (s.ProxySites, s.DirectSites) : (s.DirectSites, s.ProxySites);
                    other.RemoveAll(o => o == v);                       // правило не может быть в обоих списках
                    if (target.Contains(v)) skipped++; else { target.Add(v); added++; }
                    break;
                }
                case "app":
                {
                    var v = NormalizeApp(val, out var err);
                    if (v == null) { errors.Add($"{line} — {err}"); break; }
                    if (s.DirectApps.Contains(v, StringComparer.OrdinalIgnoreCase)) skipped++; else { s.DirectApps.Add(v); added++; }
                    break;
                }
                default: errors.Add($"{line} — неизвестная строка"); break;
            }
        }
        return new ImportResult(added, skipped, errors);
    }
}
