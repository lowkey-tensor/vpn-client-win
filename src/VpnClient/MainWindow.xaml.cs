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
        ApplyGeometry();
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
        UpdateRoutesButton();
        InitTray();
        _ = RunCoreSelfTestAsync();

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; // крестик — свернуть в трей
            SaveState();
            Hide();
        };

        if (autoConnect) _ = AutoConnectAsync();
    }

    // ---- размер и положение окна (маленькие экраны) ----
    private void ApplyGeometry()
    {
        var work = SystemParameters.WorkArea;
        var (w, h) = WindowLayout.Fit(work, _state.WinWidth, _state.WinHeight);
        Width = w; Height = h;
        if (_state.WinLeft is double l && _state.WinTop is double t && WindowLayout.IsOnScreen(work, l, t, w, h)) { Left = l; Top = t; }
        else { (Left, Top) = WindowLayout.Center(work, w, h); }
    }

    private void RememberGeometry()
    {
        if (WindowState != WindowState.Normal || double.IsNaN(Width) || double.IsNaN(Left) || Left < -10000) return;
        _state.WinLeft = Left; _state.WinTop = Top; _state.WinWidth = Width; _state.WinHeight = Height;
    }

    private void OnNodeWheel(object s, MouseWheelEventArgs e)
    {
        // список узлов не прокручивается сам — колесо листает всю страницу
        if (PageScroll.ScrollableHeight <= 0) return;
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // ---- проверка ядер при запуске ----
    private async Task RunCoreSelfTestAsync()
    {
        var problems = await Task.Run(() => CoreRunner.SelfTestAsync());
        if (problems.Count == 0)
        {
            AppendLog($"Ядра проверены: sing-box {CoreRunner.SingBoxVersion}, xray {CoreRunner.XrayVersion}");
            return;
        }
        foreach (var p in problems) AppendLog("ВНИМАНИЕ: " + p);
        ShowCoreProblem(string.Join("\n\n", problems));
        Notify("Не удаётся запустить ядро VPN. Подробности в окне программы.", System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void ShowCoreProblem(string text)
    {
        CoreWarningText.Text = text;
        CoreWarning.Visibility = Visibility.Visible;
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
        menu.Items.Add("Маршруты…", null, (_, _) => Dispatcher.Invoke(() => { ShowWindow(); OnRoutes(this, new RoutedEventArgs()); }));
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
        RememberGeometry();
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
        ModeProxy.IsEnabled = ModeTun.IsEnabled = RuBox.IsEnabled = AdBox.IsEnabled = off;   // «Маршруты» открываются и при включённом VPN
        if (v == View.On)
        {
            _connectedAt = DateTime.Now;
            _startHintShown = false;
            SubStatus.Text = _current?.Name ?? "";
            DownText.Text = UpText.Text = "0 Б/с";
            TotalText.Text = "";
            ServerPingText.Text = RealPingText.Text = "—";
            ServerPingText.Foreground = RealPingText.Foreground = new Media.SolidColorBrush(Muted);
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

    private long _rate;   // последняя суммарная скорость, байт/с: при загрузке канала отклик растёт, это не поломка

    private void OnTraffic(long up, long down) => Dispatcher.InvokeAsync(() =>
    {
        _rate = up + down;
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
    private bool _startHintShown;   // подсказку про «наплыв соединений» показываем один раз за подключение

    private static readonly Media.Color Red = Media.Color.FromRgb(0xFF, 0x6B, 0x6B);

    private static Media.Brush PingBrush(int ms, int good, int ok) =>
        new Media.SolidColorBrush(ms < good ? Green : ms < ok ? Amber : Red);

    private static async Task<int?> ServerPingAsync(NodeEntry? node)
    {
        try { return node?.Server is { } h && node.Port > 0 ? await Pinger.MeasureAsync(h, node.Port) : null; }
        catch { return null; }
    }

    private async Task HealthCheckAsync()
    {
        if (_clash == null || !_core.IsRunning || _connecting) return;
        var clash = _clash; var node = _current;

        // проверка жизни — запрос по HTTPS (как раньше); «честный» отклик — короткий запрос без шифрования; плюс пинг до сервера
        var https = clash.DelayAsync();
        var http = clash.DelayAsync("http://www.gstatic.com/generate_204");
        var server = ServerPingAsync(node);
        await Task.WhenAll(https, http, server);
        int? hs = https.Result, ht = http.Result, sp = server.Result;

        if (sp is int s) { ServerPingText.Text = $"{s} мс"; ServerPingText.Foreground = PingBrush(s, 100, 250); }
        else { ServerPingText.Text = "нет ответа"; ServerPingText.Foreground = new Media.SolidColorBrush(Muted); }

        int? shown = ht ?? hs;   // если по HTTP не ответили, показываем число от запроса по HTTPS (оно больше — ставим «≈»)
        if (shown is int ms)
        {
            bool loaded = _rate > 1_500_000;   // идёт большая загрузка: отклик и так вырастет
            string label = ht != null ? $"{ms} мс" : $"≈{ms} мс";
            string note = loaded ? " (под нагрузкой)" : "";
            if (_healthFails > 0) AppendLog($"Связь через VPN восстановилась (отклик {ms} мс{note})");
            else if (++_healthOk % 20 == 1)   // раз в ~10 минут — фон для разборов
                AppendLog($"Отклик через VPN: {ms} мс{note}, до сервера: {(sp is int s2 ? $"{s2} мс" : "нет ответа")}");
            _healthFails = 0;
            _recoverAttempts = 0;
            RealPingText.Text = label + (loaded ? " · нагрузка" : "");
            RealPingText.Foreground = loaded ? new Media.SolidColorBrush(Muted) : PingBrush(ms, 200, 500);
            return;
        }
        RealPingText.Text = "нет связи";
        RealPingText.Foreground = new Media.SolidColorBrush(Red);
        _healthFails++;
        AppendLog($"Проверка связи через VPN не прошла ({_healthFails}/3)");
        if (!_startHintShown && (DateTime.Now - _connectedAt).TotalSeconds < 120)
        {
            _startHintShown = true;
            AppendLog("Подсказка: сразу после включения VPN многие программы (Telegram, браузер) открывают десятки соединений, и это может перегрузить сервер. Подождите минуту или перезапустите эти программы. Если не помогает — смотрите раздел «Если не подключается» в README.");
        }
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

    private async void OnRoutes(object s, RoutedEventArgs e)
    {
        bool connected = _wantConnected && _core.IsRunning;
        var win = new RoutesWindow(_state, connected) { Owner = IsVisible ? this : null };
        if (win.ShowDialog() != true) return;
        SaveState();
        UpdateRoutesButton();
        AppendLog($"Маршруты сохранены: сайтов напрямую {_state.DirectSites.Count}, через VPN {_state.ProxySites.Count}, программ {_state.DirectApps.Count} " +
                  $"({(_state.AppsMode == AppsMode.OnlyListedViaVpn ? "только они через VPN" : "они мимо VPN")})");
        if (win.ReconnectRequested && _wantConnected && _current != null && !_connecting)
        {
            AppendLog("Переподключение с новыми маршрутами");
            await ConnectAsync(_current);
        }
    }

    private void UpdateRoutesButton()
    {
        int n = _state.DirectSites.Count + _state.ProxySites.Count + _state.DirectApps.Count;
        RoutesBtnText.Text = n > 0 ? $"Маршруты: сайты и программы · {n}" : "Маршруты: сайты и программы";
    }

    // ---- отчёт для поддержки ----
    private ReportInfo MakeReportInfo()
    {
        bool on = _wantConnected && _core.IsRunning;
        string? protocol = null; bool viaXray = false; string details = "";
        var node = _current ?? SelectedNode();
        if (node != null)
        {
            protocol = node.Protocol;
            try { viaXray = LinkParser.Parse(node.Link).Xray != null; } catch { }
            details = ReportBuilder.DescribeNode(node.Link, viaXray);
        }
        var work = SystemParameters.WorkArea;
        double scale = Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new ReportInfo(
            AppInfo.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            (ModeTun.IsChecked == true ? "TUN" : "системный прокси") + (on ? ", подключено" : ", отключено"),
            protocol, viaXray,
            on ? ServerPingText.Text : null, on ? RealPingText.Text : null,
            RuBox.IsChecked == true, AdBox.IsChecked == true, RecoverBox.IsChecked == true, AutoBox.IsChecked == true,
            _state.DirectSites.Count, _state.ProxySites.Count, _state.DirectApps.Count,
            _state.AppsMode == AppsMode.OnlyListedViaVpn ? "только они через VPN" : "они мимо VPN",
            CoreRunner.SingBoxVersion, CoreRunner.XrayVersion,
            $"{SystemParameters.PrimaryScreenWidth:0}×{SystemParameters.PrimaryScreenHeight:0}, рабочая область {work.Width:0}×{work.Height:0}, масштаб {scale * 100:0}%",
            details);
    }

    private string BuildReportText()
    {
        var lines = LogFile.Tail(80);
        if (lines.Count == 0) lines = LogBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(80).Select(l => l.TrimEnd('\r')).ToList();
        return ReportBuilder.Build(MakeReportInfo(), lines, _state.Nodes.Select(n => n.Name));
    }

    private bool CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) { AppendLog("Не удалось скопировать в буфер обмена: " + ex.Message); return false; }
    }

    private async void OnCopyReport(object s, RoutedEventArgs e)
    {
        if (!CopyToClipboard(BuildReportText())) return;
        AppendLog("Отчёт для поддержки скопирован в буфер обмена (без ключей, ссылок и адресов).");
        CopyReportBtn.Content = "Скопировано ✓";
        await Task.Delay(2500);
        CopyReportBtn.Content = "Скопировать отчёт";
    }

    private void OnReportIssue(object s, RoutedEventArgs e)
    {
        if (!CopyToClipboard(BuildReportText())) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportBuilder.IssueUrl(MakeReportInfo())) { UseShellExecute = true });
            AppendLog("Открыта страница обращения на GitHub. Отчёт уже в буфере обмена — вставьте его (Ctrl+V) в текст обращения.");
        }
        catch (Exception ex) { AppendLog("Не удалось открыть браузер: " + ex.Message + ". Отчёт в буфере обмена — отправьте его вручную."); }
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

            bool onlyApps = _state.AppsMode == AppsMode.OnlyListedViaVpn;
            if (onlyApps && mode != Mode.Tun) AppendLog("Режим «только выбранные программы через VPN» работает лишь в TUN — пока он не применяется, весь трафик идёт через VPN.");
            else if (onlyApps && _state.DirectApps.Count == 0) AppendLog("Режим «только выбранные программы через VPN» включён, но список программ пуст — он не применяется.");
            var clash = new ClashApi();
            var opts = new BuildOptions(ips, RuBox.IsChecked == true,
                _state.DirectApps.ToArray(), _state.DirectSites.ToArray(), _state.ProxySites.ToArray(),
                ads, clash.Port, clash.Secret, onlyApps, _state.SteamDirect);
            if (_state.SteamDirect && mode == Mode.Tun && !onlyApps) AppendLog("Игры Steam и клиент Steam идут напрямую, мимо VPN (отключается в «Маршрутах»).");
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
        catch (CoreRunner.CoreStartException ex)
        {
            // ядро не запускается из-за Windows/антивируса: повторы автовосстановления тут бесполезны
            AppendLog("Ошибка: " + ex.Message);
            ShowCoreProblem(ex.Message);
            _wantConnected = false;
            _current = null;
            SetView(View.Off);
            return false;
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
