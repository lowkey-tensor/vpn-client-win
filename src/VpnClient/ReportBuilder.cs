using System.Text;
using System.Text.RegularExpressions;

namespace VpnClient;

/// <summary>Данные для отчёта поддержки (без ключей, ссылок и адресов серверов).</summary>
public sealed record ReportInfo(
    string Version,
    string Os,
    string Mode,
    string? Protocol,
    bool ViaXray,
    string? ServerPing,
    string? Response,
    bool RuDirect,
    bool AdBlock,
    bool AutoRecover,
    bool AutoStart,
    int SitesDirect,
    int SitesVpn,
    int Apps,
    string AppsMode,
    string SingBoxVersion,
    string XrayVersion,
    string Screen);

public static class ReportBuilder
{
    public const string RepoIssues = "https://github.com/lowkey-tensor/vpn-client-win/issues/new";

    private static readonly Regex UserPath = new(@"([A-Za-z]:\\Users\\)[^\\\s'""<>|]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyLink = new(@"\b[a-z][a-z0-9+.\-]*://[^\s'""<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"\b(?:[0-9a-f]{1,4}:){4,7}[0-9a-f]{1,4}\b|(?<![0-9a-f:])(?:[0-9a-f]{1,4}:){1,6}:[0-9a-f:]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LongToken = new(@"[A-Za-z0-9_\-+/=]{32,}", RegexOptions.Compiled);

    /// <summary>
    /// Убирает из текста то, что нельзя показывать посторонним: имя учётной записи Windows, ссылки подключения,
    /// IP-адреса, длинные ключи и имена узлов.
    /// </summary>
    public static string Sanitize(string text, IEnumerable<string>? nodeNames = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string s = text;
        foreach (var name in (nodeNames ?? []).Where(n => n.Length >= 3).OrderByDescending(n => n.Length))
            s = s.Replace(name, "<узел>", StringComparison.OrdinalIgnoreCase);
        s = UserPath.Replace(s, "$1<user>");
        s = AnyLink.Replace(s, "<ссылка>");
        s = Ipv4.Replace(s, "<ip>");
        s = Ipv6.Replace(s, "<ipv6>");
        s = LongToken.Replace(s, "<ключ>");
        return s;
    }

    public static string Build(ReportInfo i, IEnumerable<string> logLines, IEnumerable<string>? nodeNames = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Отчёт VPN-клиента (без ключей, ссылок и адресов серверов)");
        sb.AppendLine($"Версия: {i.Version}");
        sb.AppendLine($"Windows: {i.Os}");
        sb.AppendLine($"Экран: {i.Screen}");
        sb.AppendLine($"Режим: {i.Mode}");
        sb.AppendLine($"Протокол узла: {i.Protocol ?? "—"}{(i.ViaXray ? " (через Xray)" : "")}");
        sb.AppendLine($"Пинг до сервера: {i.ServerPing ?? "—"}; отклик через VPN: {i.Response ?? "—"}");
        sb.AppendLine($"Ядра: sing-box {i.SingBoxVersion}, xray {i.XrayVersion}");
        sb.AppendLine($"Настройки: РФ напрямую={Yn(i.RuDirect)}, блок рекламы={Yn(i.AdBlock)}, автовосстановление={Yn(i.AutoRecover)}, автозапуск={Yn(i.AutoStart)}");
        sb.AppendLine($"Правила: сайтов напрямую {i.SitesDirect}, через VPN {i.SitesVpn}, программ {i.Apps} ({i.AppsMode})");
        sb.AppendLine();
        sb.AppendLine("Журнал (последние строки):");
        foreach (var l in logLines) sb.AppendLine("  " + Sanitize(l, nodeNames));
        return sb.ToString();
    }

    private static string Yn(bool v) => v ? "да" : "нет";

    /// <summary>Страница нового обращения на GitHub с короткой заготовкой; полный отчёт пользователь вставляет из буфера.</summary>
    public static string IssueUrl(ReportInfo i)
    {
        string title = $"Проблема в версии {i.Version}";
        string body =
            $"**Что произошло:**\n\n(опишите своими словами)\n\n" +
            $"**Версия:** {i.Version}\n**Режим:** {i.Mode}\n**Протокол:** {i.Protocol ?? "—"}\n**Windows:** {i.Os}\n\n" +
            "**Отчёт** (он уже в буфере обмена — нажмите Ctrl+V здесь):\n\n```\n\n```\n";
        return $"{RepoIssues}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }
}
