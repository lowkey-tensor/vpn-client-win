namespace VpnClient;

/// <summary>Выбор запасного узла и запасных адресов подписки, когда основной путь заблокирован.</summary>
public static class Failover
{
    /// <summary>Протоколы поверх UDP: операторы режут их чаще, чем TCP.</summary>
    public static bool IsUdp(string protocol) => protocol is "Hysteria2" or "Hysteria" or "TUIC";

    /// <summary>Сколько раз переподключаться к тому же узлу, прежде чем сменить его.</summary>
    public static int SameNodeTries(NodeEntry? node) => node != null && IsUdp(node.Protocol) ? 1 : 2;

    /// <summary>
    /// Следующий узел: с UDP-узла сначала уходим на TCP (если UDP режут, другой UDP-узел тоже не поможет),
    /// среди подходящих — отвечающий на пинг с наименьшей задержкой.
    /// </summary>
    public static NodeEntry? Next(IReadOnlyList<NodeEntry> nodes, NodeEntry? cur)
    {
        var others = nodes.Where(n => n != cur).ToList();
        if (others.Count == 0) return null;
        if (cur != null && IsUdp(cur.Protocol) && others.Any(n => !IsUdp(n.Protocol)))
            others = others.Where(n => !IsUdp(n.Protocol)).ToList();
        return others.Where(n => n.Ping is > 0).OrderBy(n => n.Ping).FirstOrDefault() ?? others[0];
    }

    /// <summary>
    /// Запасные адреса подписки: тот же порт и путь, но домены других узлов этой подписки
    /// (у нас это другие входы). IP не подставляем: сертификат выдан на домен.
    /// </summary>
    public static List<string> MirrorUrls(string url, IEnumerable<NodeEntry> nodes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return [];
        return nodes
            .Where(n => n.Sub == url && n.Server is { Length: > 0 })
            .Select(n => n.Server!)
            .Where(h => !System.Net.IPAddress.TryParse(h, out _) && !h.Equals(u.Host, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => new UriBuilder(u) { Host = h }.Uri.ToString())
            .ToList();
    }
}
