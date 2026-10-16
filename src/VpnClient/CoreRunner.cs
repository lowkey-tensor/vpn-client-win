using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VpnClient;

/// <summary>Запуск sing-box (+ Xray-core при необходимости) и управление системным прокси Windows.</summary>
public class CoreRunner
{
    private Process? _singbox;
    private Process? _xray;
    private bool _proxySet;

    public event Action<string>? Log;
    public event Action? Exited;
    public bool IsRunning => _singbox is { HasExited: false };

    private static string CoreDir => Path.Combine(AppContext.BaseDirectory, "core");
    public static string SingBoxPath => Path.Combine(CoreDir, "sing-box.exe");
    public static string XrayPath => Path.Combine(CoreDir, "xray.exe");

    public void Start(string singboxJson, Mode mode, string? xrayJson = null)
    {
        if (IsRunning) throw new InvalidOperationException("Уже запущено");
        if (!File.Exists(SingBoxPath)) throw new FileNotFoundException("Не найден sing-box.exe", SingBoxPath);
        if (xrayJson != null && !File.Exists(XrayPath))
            throw new FileNotFoundException("Для этого узла нужен Xray-core: не найден core\\xray.exe", XrayPath);

        Directory.CreateDirectory(Store.Dir);

        if (xrayJson != null)
        {
            string xp = Path.Combine(Store.Dir, "xray.json");
            File.WriteAllText(xp, xrayJson);
            _xray = Spawn(XrayPath, $"run -c \"{xp}\"", "xray");
            Thread.Sleep(600); // дать Xray поднять порт
            if (_xray.HasExited) throw new InvalidOperationException("Xray сразу завершился — см. лог");
        }

        string cfgPath = Path.Combine(Store.Dir, "config.json");
        File.WriteAllText(cfgPath, singboxJson);
        _singbox = Spawn(SingBoxPath, $"run -c \"{cfgPath}\"", null);
        // свой признак у каждого запуска: запоздавший выход старого процесса не трогает новый
        var run = _run = new RunToken();
        _singbox.Exited += (_, _) =>
        {
            if (run.Stopped) return; // остановили сами — всё уже убрано в Stop()
            ClearSystemProxy();
            KillXray();
            Exited?.Invoke();
        };

        if (mode == Mode.SystemProxy)
        {
            SetSystemProxy($"127.0.0.1:{ConfigBuilder.ProxyPort}");
            _proxySet = true;
        }

        // в конфигах ключи узла: ядра читают их один раз при старте, потом файлы не нужны
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            foreach (var f in new[] { "config.json", "xray.json" })
                try { File.Delete(Path.Combine(Store.Dir, f)); } catch { }
        });
    }

    private Process Spawn(string exe, string args, string? prefix)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = Store.Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {exe}");
        string tag = prefix == null ? "" : $"[{prefix}] ";
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(tag + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(tag + e.Data); };
        p.EnableRaisingEvents = true;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private void KillXray()
    {
        try { if (_xray is { HasExited: false }) _xray.Kill(true); } catch { }
    }

    private sealed class RunToken { public volatile bool Stopped; }
    private RunToken? _run;

    public void Stop()
    {
        if (_run != null) _run.Stopped = true;
        ClearSystemProxy();
        try { if (IsRunning) { _singbox!.Kill(true); _singbox.WaitForExit(3000); } } catch { }
        KillXray();
    }

    /// <summary>Проверка конфига sing-box без запуска; null = всё хорошо.</summary>
    public static string? Check(string configJson)
    {
        string p = Path.Combine(Path.GetTempPath(), "vpnclient-check.json");
        File.WriteAllText(p, configJson);
        try
        {
            var psi = new ProcessStartInfo(SingBoxPath, $"check -c \"{p}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var pr = Process.Start(psi)!;
            string err = pr.StandardError.ReadToEnd() + pr.StandardOutput.ReadToEnd();
            pr.WaitForExit();
            return pr.ExitCode == 0 ? null : err.Trim();
        }
        finally { try { File.Delete(p); } catch { } }
    }

    // ---- системный прокси (HKCU, без прав администратора) ----
    private const string InetKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void SetSystemProxy(string hostPort) => WriteProxy(true, hostPort);

    public void ClearSystemProxy()
    {
        if (!_proxySet) return;
        _proxySet = false;
        WriteProxy(false, null);
    }

    private static void WriteProxy(bool on, string? hostPort)
    {
        using var k = Registry.CurrentUser.OpenSubKey(InetKey, writable: true);
        if (k == null) return;
        if (on)
        {
            k.SetValue("ProxyServer", hostPort!);
            k.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>");
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        }
        else k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr h, int opt, IntPtr buf, int len);
}
