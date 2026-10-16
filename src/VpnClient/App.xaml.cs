using System.Windows;

namespace VpnClient;

public partial class App : Application
{
    private static Mutex? _single;
    private const string ShowEventName = @"Local\VpnClient-show-window";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool auto = e.Args.Contains("--autoconnect");

        // только один экземпляр: повторный запуск просит первый показать окно и выходит
        _single = new Mutex(true, @"Local\VpnClient-single-instance", out bool first);
        if (!first)
        {
            if (!auto)
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
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
