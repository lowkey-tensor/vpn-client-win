using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Media = System.Windows.Media;

namespace VpnClient;

public partial class MainWindow : Window
{
    private readonly State _state = Store.Load();
    private readonly CoreRunner _core = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayConnect;
    private bool _exiting;

    // состояние подключения
    private bool _wantConnected;      // пользователь хочет быть подключённым (для автовосстановления)
    private bool _connecting;
    private NodeEntry? _current;
    private ClashApi? _clash;
    private int _healthFails;         // подряд неудачных проверок связи
    private int _healthOk;            // удачных проверок (для редкой записи пинга в журнал)
    private int _recoverAttempts;     // попыток восстановления подряд
    private DateTime _connectedAt;

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _health = new() { Interval = TimeSpan.FromSeconds(30) };

    private static readonly Media.Color Gray = Media.Color.FromRgb(0x3A, 0x41, 0x50);
    private static readonly Media.Color Amber = Media.Color.FromRgb(0xF5, 0xB7, 0x3B);
    private static readonly Media.Color Green = Media.Color.FromRgb(0x2B, 0xD4, 0x7D);
    private static readonly Media.Color Muted = Media.Color.FromRgb(0x8A, 0x93, 0xA3);

    public MainWindow(bool autoConnect)
    {
        InitializeComponent();
        VersionText.Text = "v" + AppInfo.Version;
        Title = $"VPN-клиент {AppInfo.Version}";
        SourceInitialized += (_, _) => ApplyDark(this);
        Icon = WindowIcon();
        Loaded += (_, _) => _ = PingAllAsync();

        ModeTun.IsChecked = _state.Mode == Mode.Tun;
        ModeProxy.IsChecked = _state.Mode != Mode.Tun;
        AutoBox.IsChecked = AutoStart.IsEnabled();
        RuBox.IsChecked = _state.RuDirect;
        AdBox.IsChecked = _state.AdBlock;
        RecoverBox.IsChecked = _state.AutoRecover;

        _clock.Tick += async (_, _) => await UpdateStatsAsync();
        _health.Tick += async (_, _) => await HealthCheckAsync();

        _core.Log += l => Dispatcher.Invoke(() => AppendLog(l));
        _core.Exited += () => Dispatcher.Invoke(OnCoreExited);

        // после сна/гибернации туннель обычно мёртв — переподключаемся
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.InvokeAsync(async () =>
                {
                    if (!_wantConnected) return;
                    await Task.Delay(5000);
                    await RecoverAsync("после выхода из сна", sameNodeOnly: true);
                });
        };

        // смена сети Windows (Wi-Fi/кабель моргнул) — пишем в журнал для разборов «почему булькнуло»
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
            Dispatcher.InvokeAsync(() => AppendLog(e.IsAvailable ? "Сеть Windows: доступна" : "Сеть Windows: ПРОПАЛА"));

        LogFile.Rotate();
        AppendLog($"Клиент запущен, версия {AppInfo.Version}" + (autoConnect ? " (автозапуск)" : ""));
        RefreshList();
        InitTray();

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; // крестик — свернуть в трей
            SaveState();
            Hide();
        };

        if (autoConnect) _ = AutoConnectAsync();
    }

    public static void ApplyDark(Window w)
    {
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- трей ----
    private void InitTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem($"VPN-клиент {AppInfo.Version}") { Enabled = false });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add("Папка журналов", null, (_, _) =>
        {
            System.IO.Directory.CreateDirectory(LogFile.Dir);
            System.Diagnostics.Process.Start("explorer.exe", LogFile.Dir);
        });
        _trayConnect = new System.Windows.Forms.ToolStripMenuItem("Подключить", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add(_trayConnect);
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = IconArt.TrayIcon(false),
            Text = $"VPN-клиент {AppInfo.Version}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void Notify(string text, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info) =>
        _tray?.ShowBalloonTip(4000, "VPN-клиент", text, icon);

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        Cleanup();
        Application.Current.Shutdown();
    }

    public void Cleanup()
    {
        _exiting = true;
        _wantConnected = false;
        SaveState();
        StopCore();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    private async Task AutoConnectAsync()
    {
        await Task.Delay(8000); // дать Windows поднять сеть
        for (int attempt = 1; attempt <= 3 && !_core.IsRunning; attempt++)
        {
            if (await ConnectAsync(SelectedNode())) return;
            await Task.Delay(10000);
        }
        Notify("Не удалось подключиться автоматически", System.Windows.Forms.ToolTipIcon.Warning);
    }

    // ---- общее ----
    private void AppendLog(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", ""); // цвета ядра
        string line = $"{DateTime.Now:HH:mm:ss} {s}";
        if (LogBox.LineCount > 500) LogBox.Clear();
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        LogFile.Write(line);
    }

    private void RefreshList()
    {
        NodeList.Items.Clear();
        foreach (var n in _state.Nodes) NodeList.Items.Add(n);
        var sel = SelectedNode();
        if (sel != null) NodeList.SelectedItem = sel;
        EmptyHint.Visibility = _state.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private NodeEntry? SelectedNode() =>
        _state.Nodes.FirstOrDefault(x => x.Link == _state.SelectedLink) ?? _state.Nodes.FirstOrDefault();

    private static Media.ImageSource WindowIcon()
    {
        using var bmp = IconArt.Render(64, IconArt.Blue);
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new Media.Imaging.BitmapImage();
        img.BeginInit(); img.CacheOption = Media.Imaging.BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
        return img;
    }

    private void SaveState()
    {
        _state.Mode = ModeTun.IsChecked == true ? Mode.Tun : Mode.SystemProxy;
        _state.RuDirect = RuBox.IsChecked == true;
        _state.AdBlock = AdBox.IsChecked == true;
        _state.AutoRecover = RecoverBox.IsChecked == true;
        Store.Save(_state);
    }

    // ---- пинг узлов (до сервера) ----
    private bool _pinging;

    private void OnPing(object s, RoutedEventArgs e) => _ = PingAllAsync();

    private async Task PingAllAsync()
    {
        if (_pinging) return;
        _pinging = true;
        try
        {
            var nodes = _state.Nodes.ToList();
            foreach (var n in nodes) n.Ping = 0; // «…»
            await Task.WhenAll(nodes.Select(async n =>
            {
                try
                {
                    n.Ping = n.Server is { } host && n.Port > 0
                        ? await Pinger.MeasureAsync(host, n.Port) ?? -1
                        : -1;
                }
                catch { n.Ping = -1; }
            }));
        }
        finally { _pinging = false; }
    }

    // ---- вид: отключено / подключение / подключено ----
    private enum View { Off, Connecting, On }

    private void SetView(View v)
    {
        var color = v switch { View.On => Green, View.Connecting => Amber, _ => Gray };
        ConnectBtn.Background = new Media.SolidColorBrush(color);
        StatusText.Text = v switch { View.On => "Защищено", View.Connecting => "Подключение…", _ => "Нажмите, чтобы подключиться" };
        StatusPillText.Text = v switch { View.On => "Подключено", View.Connecting => "Подключение", _ => "Отключено" };
        StatusPillText.Foreground = StatusDot.Fill = new Media.SolidColorBrush(v == View.Off ? Muted : color);
        StatsPanel.Visibility = v == View.On ? Visibility.Visible : Visibility.Collapsed;

        bool off = v == View.Off;
        ModeProxy.IsEnabled = ModeTun.IsEnabled = RuBox.IsEnabled = AdBox.IsEnabled = RoutesBtn.IsEnabled = off;
        if (v == View.On)
        {
            _connectedAt = DateTime.Now;
            SubStatus.Text = _current?.Name ?? "";
            DownText.Text = UpText.Text = "0 Б/с";
            TotalText.Text = RealPingText.Text = "";
            _clock.Start();
            _health.Start();
        }
        else
        {
            _clock.Stop();
            _health.Stop();
            if (off) SubStatus.Text = " ";
        }

        if (_trayConnect != null) _trayConnect.Text = off ? "Подключить" : "Отключить";
        if (_tray != null)
        {
            _tray.Text = v == View.On ? $"VPN {AppInfo.Version}: {Trim(_current?.Name, 40)}"
                       : off ? $"VPN-клиент {AppInfo.Version}: отключено" : $"VPN-клиент {AppInfo.Version}: подключение…";
            var old = _tray.Icon;
            _tray.Icon = IconArt.TrayIcon(v == View.On);
            old?.Dispose();
        }
    }

    private static string Trim(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---- скорость, трафик, время ----
    private async Task UpdateStatsAsync()
    {
        var t = DateTime.Now - _connectedAt;
        SubStatus.Text = $"{_current?.Name} · {t:hh\\:mm\\:ss}";
        if (_clash?.TotalsAsync() is { } task && await task is { } tot)
            TotalText.Text = $"Σ {Bytes(tot.down + tot.up)}";
    }

    private void OnTraffic(long up, long down) => Dispatcher.InvokeAsync(() =>
    {
        DownText.Text = Bytes(down) + "/с";
        UpText.Text = Bytes(up) + "/с";
        if (_tray != null && _current != null)
            _tray.Text = Trim($"VPN: {_current.Name} ↓{Bytes(down)}/с", 63);
    });

    private static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} Б",
        < 1024 * 1024 => $"{b / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} МБ",
        _ => $"{b / 1073741824.0:0.##} ГБ",
    };

    // ---- автовосстановление ----
    private async Task HealthCheckAsync()
    {
        if (_clash == null || !_core.IsRunning || _connecting) return;
        var d = await _clash.DelayAsync();
        if (d is int ms)
        {
            if (_healthFails > 0) AppendLog($"Связь через VPN восстановилась ({ms} мс)");
            else if (++_healthOk % 20 == 1) AppendLog($"Пинг через VPN: {ms} мс"); // раз в ~10 минут — фон для разборов
            _healthFails = 0;
            _recoverAttempts = 0;
            RealPingText.Text = $"{ms} мс";
            RealPingText.Foreground = new Media.SolidColorBrush(ms < 150 ? Green : ms < 400 ? Amber : Media.Color.FromRgb(0xFF, 0x6B, 0x6B));
            return;
        }
        RealPingText.Text = "нет связи";
        RealPingText.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(0xFF, 0x6B, 0x6B));
        _healthFails++;
        AppendLog($"Проверка связи через VPN не прошла ({_healthFails}/3)");
        if (_healthFails >= 3 && RecoverBox.IsChecked == true)
            await RecoverAsync("нет связи через VPN");
    }

    /// <summary>Ядро завершилось само (намеренные остановки CoreRunner сюда не сообщает).</summary>
    private void OnCoreExited()
    {
        _clash?.Dispose(); _clash = null;
        if (_wantConnected && RecoverBox.IsChecked == true && !_exiting)
        {
            AppendLog("Ядро неожиданно остановилось");
            _ = RecoverAsync("ядро остановилось");
            return;
        }
        _wantConnected = false;
        _current = null;
        SetView(View.Off);
        AppendLog("Отключено");
    }

    /// <summary>Переподключение: дважды тот же узел, потом — следующий рабочий.</summary>
    private async Task RecoverAsync(string reason, bool sameNodeOnly = false)
    {
        if (_connecting || !_wantConnected) return;
        _recoverAttempts++;
        _healthFails = 0;

        var node = _current ?? SelectedNode();
        if (!sameNodeOnly && _recoverAttempts > 2 && NextNode(node) is { } next)
        {
            AppendLog($"Узел «{node?.Name}» не отвечает — переключаюсь на «{next.Name}»");
            Notify($"Переключился на «{next.Name}»");
            node = next;
            _recoverAttempts = 1;
        }
        else AppendLog($"Переподключение ({reason}), попытка {_recoverAttempts}");

        if (await ConnectAsync(node)) return;

        // не вышло — пробуем ещё, с паузой (сеть могла пропасть совсем)
        int delay = _recoverAttempts > 6 ? 60 : 10;
        AppendLog($"Повтор через {delay} с");
        SetView(View.Connecting);
        await Task.Delay(delay * 1000);
        if (_wantConnected && !_core.IsRunning) await RecoverAsync(reason, sameNodeOnly);
    }

    /// <summary>Следующий по кругу узел, предпочитая отвечающие на пинг.</summary>
    private NodeEntry? NextNode(NodeEntry? cur)
    {
        var others = _state.Nodes.Where(n => n != cur).ToList();
        if (others.Count == 0) return null;
        return others.Where(n => n.Ping is > 0).OrderBy(n => n.Ping).FirstOrDefault() ?? others[0];
    }

    // ---- добавление ----
    private async void OnAdd(object s, RoutedEventArgs e)
    {
        string text = InputBox.Text.Trim();
        if (text == "") return;
        try
        {
            if (text.StartsWith("http://") || text.StartsWith("https://"))
            {
                if (!_state.Subs.Contains(text)) _state.Subs.Add(text);
                await LoadSubscription(text);
            }
            else
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddLink(line, null);
            }
            InputBox.Clear();
            SaveState();
            RefreshList();
            _ = PingAllAsync();
        }
        catch (Exception ex) { AppendLog("Ошибка: " + ex.Message); }
    }

    private void AddLink(string link, string? sub)
    {
        try
        {
            var p = LinkParser.Parse(link);
            if (_state.Nodes.Any(n => n.Link == link)) return;
            _state.Nodes.Add(new NodeEntry { Name = p.Name, Link = link, Sub = sub });
        }
        catch (Exception ex) { AppendLog($"Пропущена ссылка: {ex.Message}"); }
    }

    private async Task LoadSubscription(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("v2rayN/6.0");
        string body = await (await Http.SendAsync(req)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        var links = LinkParser.SplitSubscription(body).ToList();
        _state.Nodes.RemoveAll(n => n.Sub == url && n != _current);
        foreach (var l in links) AddLink(l, url);
        AppendLog($"Подписка обновлена: {_state.Nodes.Count(n => n.Sub == url)} узл.");
    }

    private async void OnRefresh(object s, RoutedEventArgs e)
    {
        foreach (var url in _state.Subs.ToList())
        {
            try { await LoadSubscription(url); }
            catch (Exception ex) { AppendLog("Ошибка подписки: " + ex.Message); }
        }
        SaveState();
        RefreshList();
        _ = PingAllAsync();
    }

    private async void OnSelect(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NodeList.SelectedItem is not NodeEntry n) return;
        _state.SelectedLink = n.Link;
        // подключены — выбор другого узла сразу переключает на него
        if (_wantConnected && _current != null && n != _current && !_connecting)
        {
            _recoverAttempts = 0;
            AppendLog($"Переключение на «{n.Name}»");
            await ConnectAsync(n);
        }
    }

    private void OnListKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && NodeList.SelectedItem is NodeEntry n) DeleteNode(n);
    }

    private void OnDeleteNode(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: NodeEntry n }) DeleteNode(n);
    }

    private void DeleteNode(NodeEntry n)
    {
        if (n == _current && _wantConnected) { AppendLog("Это активный узел — сначала отключитесь"); return; }
        if (MessageBox.Show(this, $"Удалить конфиг «{n.Name}»?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _state.Nodes.Remove(n);
        if (_state.SelectedLink == n.Link) _state.SelectedLink = null;
        // подписка без единого узла больше не нужна
        if (n.Sub != null && !_state.Nodes.Any(x => x.Sub == n.Sub)) _state.Subs.Remove(n.Sub);
        SaveState();
        RefreshList();
    }

    private void OnQr(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: NodeEntry n })
            new QrWindow(n) { Owner = this }.ShowDialog();
    }

    private void OnInputKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAdd(s, e);
    }

    private void OnRoutes(object s, RoutedEventArgs e)
    {
        if (new RoutesWindow(_state) { Owner = this }.ShowDialog() == true)
        {
            SaveState();
            AppendLog($"Маршруты сохранены: программ напрямую {_state.DirectApps.Count}, сайтов напрямую {_state.DirectSites.Count}, через VPN {_state.ProxySites.Count}");
        }
    }

    private void OnRecoverToggle(object s, RoutedEventArgs e) => SaveState();

    // ---- подключение ----
    private void OnConnect(object s, RoutedEventArgs e) => Toggle();

    private async void Toggle()
    {
        if (_connecting) return;
        if (_wantConnected || _core.IsRunning)
        {
            _wantConnected = false;
            StopCore();
            _current = null;
            SetView(View.Off);
            AppendLog("Отключено");
        }
        else
        {
            _recoverAttempts = 0;
            await ConnectAsync(SelectedNode());
        }
    }

    private void StopCore()
    {
        _core.Stop();
        _clash?.Dispose();
        _clash = null;
    }

    private async Task<bool> ConnectAsync(NodeEntry? n)
    {
        if (n == null) { AppendLog("Нет узлов — добавьте ссылку"); return false; }
        if (_connecting) return false;
        _connecting = true;
        _wantConnected = true;
        SetView(View.Connecting);
        try
        {
            if (_core.IsRunning) { StopCore(); await Task.Delay(1500); } // переключение узла

            var mode = ModeTun.IsChecked == true ? Mode.Tun : Mode.SystemProxy;
            var p = LinkParser.Parse(n.Link);
            var ips = await Task.Run(() => ResolveServer(p.Server));

            string? ads = null;
            if (AdBox.IsChecked == true)
            {
                if (!RuleSets.HasAds)
                {
                    AppendLog("Скачиваю список рекламных доменов…");
                    await RuleSets.UpdateAdsAsync();
                }
                if (RuleSets.HasAds) ads = RuleSets.AdsPath;
                else AppendLog("Блок рекламы пока пропущен: список не скачался, попробую после подключения");
            }

            var clash = new ClashApi();
            var opts = new BuildOptions(ips, RuBox.IsChecked == true,
                _state.DirectApps.ToArray(), _state.DirectSites.ToArray(), _state.ProxySites.ToArray(),
                ads, clash.Port, clash.Secret);
            string cfg = ConfigBuilder.Build(p.Outbound, mode, opts);

            string? err = await Task.Run(() => CoreRunner.Check(cfg));
            if (err != null && ads != null)
            {
                // повреждённый список рекламы не должен ломать подключение
                AppendLog("Список рекламы не подошёл ядру — подключаюсь без него");
                cfg = ConfigBuilder.Build(p.Outbound, mode, opts with { AdsRuleSetPath = null });
                err = await Task.Run(() => CoreRunner.Check(cfg));
                ads = null;
            }
            if (err != null) { AppendLog("Конфиг не прошёл проверку: " + err); clash.Dispose(); FailView(); return false; }

            _core.Start(cfg, mode, p.Xray == null ? null : ConfigBuilder.BuildXray(p.Xray));
            _clash = clash;
            _clash.Traffic += OnTraffic;
            _clash.StartTraffic();
            _current = n;
            _healthFails = 0;
            _state.Mode = mode;
            _state.SelectedLink = n.Link;
            SetView(View.On);
            SaveState(); // автозапуск подключит этот же узел и режим
            AppendLog($"Подключено: {n.Name} ({(mode == Mode.Tun ? "TUN" : "прокси")}{(p.Xray != null ? ", через Xray" : "")}{(ads != null ? ", без рекламы" : "")})");

            // первая проверка связи сразу, не через 30 с
            _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.InvokeAsync(HealthCheckAsync));
            // список рекламы устарел/не скачался — обновим уже через туннель
            if (AdBox.IsChecked == true && RuleSets.AdsStale)
                _ = RuleSets.UpdateAdsAsync(mode == Mode.SystemProxy ? $"http://127.0.0.1:{ConfigBuilder.ProxyPort}" : null);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            FailView();
            return false;
        }
        finally { _connecting = false; }
    }

    private void FailView()
    {
        // при автовосстановлении остаёмся в «Подключение…», иначе — выключено
        if (!(_wantConnected && RecoverBox.IsChecked == true && _current != null))
        {
            _wantConnected = false;
            SetView(View.Off);
        }
    }

    private IEnumerable<string> ResolveServer(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip)) return [ip.ToString()];
        try { return System.Net.Dns.GetHostAddresses(host).Select(a => a.ToString()).ToArray(); }
        catch (Exception ex) { Dispatcher.Invoke(() => AppendLog($"Не удалось получить IP сервера {host}: {ex.Message}")); return []; }
    }

    // ---- автозапуск ----
    private void OnAutoStart(object s, RoutedEventArgs e)
    {
        bool want = AutoBox.IsChecked == true;
        bool ok = want ? AutoStart.Enable() : AutoStart.Disable();
        if (!ok)
        {
            AppendLog("Не удалось изменить автозапуск");
            AutoBox.IsChecked = AutoStart.IsEnabled();
            return;
        }
        SaveState();
        AppendLog(want ? "Автозапуск включён: при входе в Windows клиент стартует в трее и подключается"
                       : "Автозапуск выключен");
    }
}
