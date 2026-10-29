using System.IO;
using System.Windows;

namespace VpnClient;

public partial class App : Application
{
    private static Mutex? _single;
    private const string ShowEventName = @"Local\VpnClient-show-window";

    private static string? OtherInstancePath()
    {
        try
        {
            int me = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("VpnClient"))
            {
                try { if (p.Id != me && p.MainModule?.FileName is { Length: > 0 } f) return f; } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool auto = e.Args.Contains("--autoconnect");

        // только один экземпляр: повторный запуск просит первый показать окно и выходит
        _single = new Mutex(true, @"Local\VpnClient-single-instance", out bool first);
        if (!first)
        {
            if (!auto)
            {
                // запущена копия из другой папки (например, из архива): тихо показать её окно — значит, человек не поймёт, почему «не то»
                string? other = OtherInstancePath();
                string me = Environment.ProcessPath ?? "";
                if (other != null && me.Length > 0 && !string.Equals(Path.GetFullPath(other), Path.GetFullPath(me), StringComparison.OrdinalIgnoreCase))
                    MessageBox.Show($"Клиент уже запущен из другой папки:\n{other}\n\nЭта копия не запустится, пока работает та. Закройте ту (значок в трее → «Выход»), затем запустите эту снова.",
                        "VPN-клиент", MessageBoxButton.OK, MessageBoxImage.Information);
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            }
            Shutdown();
            return;
        }

        var w = new MainWindow(auto);
        // явно Normal: если нас запустил скрытый процесс (скрипт обновления), окно не должно унаследовать «скрыто»
        if (!auto) { w.WindowState = WindowState.Normal; w.Show(); w.Activate(); }
        SessionEnding += (_, _) => w.Cleanup();

        // ждём сигнала «покажи окно» от повторных запусков
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (show.WaitOne())
                w.Dispatcher.Invoke(() => { w.Show(); w.WindowState = WindowState.Normal; w.Activate(); });
        }) { IsBackground = true };
        t.Start();
    }
}
