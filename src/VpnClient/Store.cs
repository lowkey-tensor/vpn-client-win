using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace VpnClient;

public class NodeEntry : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    // пинг: null — не измерен, -1 — недоступен
    private int? _ping;
    [JsonIgnore] public int? Ping
    {
        get => _ping;
        set
        {
            _ping = value;
            PropertyChanged?.Invoke(this, new(nameof(PingText)));
            PropertyChanged?.Invoke(this, new(nameof(PingBrush)));
        }
    }
    [JsonIgnore] public string PingText => _ping switch { null => "", -1 => "нет ответа", 0 => "…", var v => $"{v} мс" };
    [JsonIgnore] public Brush PingBrush => new SolidColorBrush(_ping switch
    {
        null or 0 => Color.FromRgb(0x8A, 0x93, 0xA3),
        -1 => Color.FromRgb(0xFF, 0x6B, 0x6B),
        < 100 => Color.FromRgb(0x2B, 0xD4, 0x7D),
        < 250 => Color.FromRgb(0xF5, 0xB7, 0x3B),
        _ => Color.FromRgb(0xFF, 0x6B, 0x6B),
    });

    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
    public string? Sub { get; set; }
    public string Protocol => LinkParser.Protocol(Link);
    public override string ToString() => $"{Name}  [{Protocol}]";

    // для карточки в списке
    [JsonIgnore] public string Badge => Protocol switch
    {
        "Hysteria2" => "HY2",
        "Hysteria" => "HY",
        "Shadowsocks" => "SS",
        var p => p.ToUpperInvariant(),
    };

    [JsonIgnore] public Brush BadgeBrush => new SolidColorBrush(Protocol switch
    {
        "VLESS" => Color.FromRgb(0x7C, 0x5C, 0xFF),
        "VMess" => Color.FromRgb(0x3B, 0x82, 0xF6),
        "Trojan" => Color.FromRgb(0xE1, 0x5B, 0x3D),
        "Shadowsocks" => Color.FromRgb(0x22, 0xA5, 0x5E),
        "TUIC" => Color.FromRgb(0xD9, 0x46, 0xA6),
        "Hysteria2" => Color.FromRgb(0x14, 0xB8, 0xA6),
        "Hysteria" => Color.FromRgb(0xC2, 0x8A, 0x1E),
        "AnyTLS" => Color.FromRgb(0x06, 0x91, 0xB2),
        _ => Color.FromRgb(0x64, 0x74, 0x8B),
    });

    /// <summary>Адрес сервера из ссылки (null, если ссылку не удалось разобрать).</summary>
    [JsonIgnore] public string? Server => LinkParser.Endpoint(Link)?.Host;
    [JsonIgnore] public int Port => LinkParser.Endpoint(Link)?.Port ?? 0;
    [JsonIgnore] public string Host => LinkParser.Endpoint(Link) is { } e
        ? (e.Host.Contains(':') ? $"[{e.Host}]:{e.Port}" : $"{e.Host}:{e.Port}")
        : "";
}

public class State
{
    public List<NodeEntry> Nodes { get; set; } = new();
    public List<string> Subs { get; set; } = new();
    public string? SelectedLink { get; set; }
    public Mode Mode { get; set; } = Mode.SystemProxy;
    public bool RuDirect { get; set; } = true;
    public bool AdBlock { get; set; } = true;
    public bool AutoRecover { get; set; } = true;
    // раздельный туннель
    public List<string> DirectApps { get; set; } = new();   // имена exe: мимо VPN или «только эти через VPN» (см. AppsMode)
    public List<string> DirectSites { get; set; } = new();  // домены и IP — мимо VPN
    public List<string> ProxySites { get; set; } = new();   // домены и IP — всегда через VPN
    public AppsMode AppsMode { get; set; } = AppsMode.DirectList;
    // положение и размер окна (запоминаем, чтобы на ноутбуке не подгонять каждый раз)
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
}

/// <summary>Состояние хранится в %APPDATA%\VpnClient\state.dat, зашифровано DPAPI (только этот пользователь Windows).</summary>
public static class Store
{
    // VPNCLIENT_DATA_DIR переносит данные, журналы и временные конфиги в другую папку (тесты, портативный запуск)
    public static readonly string Dir = Environment.GetEnvironmentVariable("VPNCLIENT_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpnClient");
    private static string FilePath => Path.Combine(Dir, "state.dat");

    public static State Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new State();
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<State>(Encoding.UTF8.GetString(raw)) ?? new State();
        }
        catch
        {
            // не потерять узлы из-за нечитаемого файла: откладываем его в сторону, прежде чем начнётся новое состояние
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            return new State();
        }
    }

    public static void Save(State s)
    {
        Directory.CreateDirectory(Dir);
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s));
        File.WriteAllBytes(FilePath, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
    }
}
