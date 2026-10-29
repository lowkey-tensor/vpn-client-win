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

    // папка самого exe, а не AppContext.BaseDirectory: в однофайловой сборке они могут различаться
    private static string CoreDir => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "core");
    public static string SingBoxPath => Path.Combine(CoreDir, "sing-box.exe");
    public static string XrayPath => Path.Combine(CoreDir, "xray.exe");

    // ---- понятные причины, когда Windows не даёт запустить ядро ----

    /// <summary>Ошибка запуска ядра с объяснением на человеческом языке.</summary>
    public sealed class CoreStartException : Exception
    {
        public CoreStartException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>Программу открыли двойным щелчком прямо внутри zip: Windows распаковала во временную папку только exe, без папки core.</summary>
    public static bool RunFromArchive(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath ?? "";
        if (exePath.Contains(".zip", StringComparison.OrdinalIgnoreCase)) return true;
        try { return exePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public const string FromArchiveMessage =
        "Программа запущена прямо из архива, поэтому рядом с ней нет папки core. Закройте её (значок в трее → «Выход»), " +
        "распакуйте zip целиком (правая кнопка на архиве → «Извлечь всё…») и запустите VpnClient.exe из получившейся папки.";

    /// <summary>Переводит ошибку Windows при запуске exe в понятную причину и подсказку, что делать.</summary>
    public static string Explain(Exception ex, string exePath)
    {
        if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 } && RunFromArchive(exePath))
            return FromArchiveMessage;
        string name = Path.GetFileName(exePath);
        string dir = Path.GetDirectoryName(exePath) ?? "";
        int code = ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : 0;
        string reason = code switch
        {
            2 or 3 => $"файл «{name}» не найден — скорее всего, его удалил антивирус",
            5 => "доступ к файлу запрещён — его блокирует антивирус или защита Windows",
            193 => "файл повреждён или не подходит к этой Windows — скачайте архив заново",
            225 or 226 => "антивирус заблокировал файл как «потенциально нежелательный»",
            740 => "нужны права администратора",
            786 or 1260 or 1261 or 4551 => "политика безопасности Windows (Smart App Control или AppLocker) запрещает запуск неподписанных программ",
            _ => ex.Message,
        };
        bool blocked = code is 2 or 3 or 5 or 225 or 226 or 786 or 1260 or 1261 or 4551;
        string hint = blocked
            ? $" Что сделать: откройте «Защита Windows → Защита от вирусов → Журнал защиты», разрешите «{name}», добавьте папку «{dir}» в исключения и, если файл удалён, распакуйте архив программы заново."
            : code == 193 ? " Проверьте контрольную сумму архива (файл .sha256 на странице релиза) и распакуйте его целиком." : "";
        return $"Windows не дала запустить «{name}»: {reason}" + (code > 0 ? $" (код {code})." : ".") + hint;
    }

    // версии ядер для отчёта поддержки (заполняются самопроверкой)
    public static string SingBoxVersion { get; private set; } = "?";
    public static string XrayVersion { get; private set; } = "?";

    /// <summary>Проверяет при запуске, что оба ядра на месте и запускаются. Пустой список — всё хорошо.</summary>
    public static async Task<List<string>> SelfTestAsync()
    {
        var problems = new List<string>();
        foreach (var (path, label) in new[] { (SingBoxPath, "sing-box"), (XrayPath, "xray") })
        {
            if (!File.Exists(path))
            {
                if (RunFromArchive()) { if (!problems.Contains(FromArchiveMessage)) problems.Add(FromArchiveMessage); continue; }
                problems.Add($"Не найден core\\{label}.exe в папке «{Path.GetDirectoryName(path)}». Распакуйте архив программы целиком: папка core должна лежать рядом с VpnClient.exe. Если файла нет после распаковки, его удалил антивирус.");
                continue;
            }
            try
            {
                var psi = new ProcessStartInfo(path, "version")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var pr = Process.Start(psi) ?? throw new InvalidOperationException("процесс не создан");
                var outTask = pr.StandardOutput.ReadToEndAsync();
                if (!pr.WaitForExit(15000)) { try { pr.Kill(true); } catch { } problems.Add($"«{label}.exe» не ответил за 15 секунд — возможно, антивирус проверяет файл."); continue; }
                string first = ((await outTask).Split('\n').FirstOrDefault() ?? "").Trim();
                if (pr.ExitCode != 0) { problems.Add($"«{label}.exe» завершился с ошибкой (код {pr.ExitCode}). Скачайте архив заново."); continue; }
                string ver = first.Split(' ').FirstOrDefault(t => t.Length > 0 && char.IsDigit(t[0])) ?? first;
                if (label == "sing-box") SingBoxVersion = ver; else XrayVersion = ver;
            }
            catch (Exception ex) { problems.Add(Explain(ex, path)); }
        }
        return problems;
    }

    public void Start(string singboxJson, Mode mode, string? xrayJson = null)
    {
        if (IsRunning) throw new InvalidOperationException("Уже запущено");
        if ((!File.Exists(SingBoxPath) || (xrayJson != null && !File.Exists(XrayPath))) && RunFromArchive())
            throw new CoreStartException(FromArchiveMessage);
        if (!File.Exists(SingBoxPath))
            throw new CoreStartException($"Не найден core\\sing-box.exe в папке «{Path.GetDirectoryName(SingBoxPath)}». Распакуйте архив программы целиком (папка core должна лежать рядом с VpnClient.exe); если файла нет после распаковки, его удалил антивирус.");
        if (xrayJson != null && !File.Exists(XrayPath))
            throw new CoreStartException($"Для этого узла нужен Xray-core, но core\\xray.exe не найден в папке «{Path.GetDirectoryName(XrayPath)}». Распакуйте архив целиком; если файла нет, его удалил антивирус.");

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
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {exe}"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new CoreStartException(Explain(ex, exe), ex); }
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
            Process pr;
            try { pr = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception ex) { throw new CoreStartException(Explain(ex, SingBoxPath), ex); }
            using (pr)
            {
                string err = pr.StandardError.ReadToEnd() + pr.StandardOutput.ReadToEnd();
                pr.WaitForExit();
                return pr.ExitCode == 0 ? null : err.Trim();
            }
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
