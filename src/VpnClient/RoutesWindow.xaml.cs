using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Media = System.Windows.Media;

namespace VpnClient;

public sealed class SiteRuleVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Value { get; }
    private bool _isVpn;
    public SiteRuleVm(string value, bool isVpn) { Value = value; _isVpn = isVpn; }
    public bool IsVpn
    {
        get => _isVpn;
        set { _isVpn = value; PropertyChanged?.Invoke(this, new(nameof(IsVpn))); PropertyChanged?.Invoke(this, new(nameof(Badge))); PropertyChanged?.Invoke(this, new(nameof(BadgeBrush))); }
    }
    public string Badge => _isVpn ? "ЧЕРЕЗ VPN" : "НАПРЯМУЮ";
    public Media.Brush BadgeBrush => new Media.SolidColorBrush(_isVpn ? Media.Color.FromRgb(0x4F, 0x7C, 0xFF) : Media.Color.FromRgb(0x47, 0x55, 0x69));
}

public sealed record AppRuleVm(string Name);

public partial class RoutesWindow : Window
{
    private readonly State _state;
    private readonly ObservableCollection<SiteRuleVm> _sites = new();
    private readonly ObservableCollection<AppRuleVm> _apps = new();

    /// <summary>Пользователь нажал «Сохранить и переподключить».</summary>
    public bool ReconnectRequested { get; private set; }

    public RoutesWindow(State state, bool connected = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.ApplyDark(this);
        _state = state;

        // окно не должно быть выше экрана (ноутбуки)
        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 40));
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 40));

        Load(state);
        SitesList.ItemsSource = _sites;
        AppsList.ItemsSource = _apps;
        _sites.CollectionChanged += (_, _) => RefreshEmpty();
        _apps.CollectionChanged += (_, _) => RefreshEmpty();
        RefreshEmpty();
        UpdateAppsHint();

        if (connected)
        {
            // VPN включён: правила применяются только после переподключения, поэтому оно — главная кнопка
            SaveReconnectBtn.Visibility = Visibility.Visible;
            SaveBtn.Style = (Style)FindResource("Btn");
            SaveBtn.Content = "Только сохранить";
            ApplyHint.Text = "Сейчас VPN включён: новые правила заработают после переподключения.";
        }
    }

    private void Load(State s)
    {
        _sites.Clear(); _apps.Clear();
        foreach (var d in s.ProxySites) _sites.Add(new SiteRuleVm(d, true));
        foreach (var d in s.DirectSites) _sites.Add(new SiteRuleVm(d, false));
        foreach (var a in s.DirectApps) _apps.Add(new AppRuleVm(a));
        AppsOnlyRb.IsChecked = s.AppsMode == AppsMode.OnlyListedViaVpn;
        AppsDirectRb.IsChecked = AppsOnlyRb.IsChecked != true;
        SteamBox.IsChecked = s.SteamDirect;
    }

    /// <summary>Текущее содержимое окна как состояние (для сохранения и копирования).</summary>
    private State Snapshot() => new()
    {
        ProxySites = _sites.Where(r => r.IsVpn).Select(r => r.Value).ToList(),
        DirectSites = _sites.Where(r => !r.IsVpn).Select(r => r.Value).ToList(),
        DirectApps = _apps.Select(a => a.Name).ToList(),
        AppsMode = AppsOnlyRb.IsChecked == true ? AppsMode.OnlyListedViaVpn : AppsMode.DirectList,
        SteamDirect = SteamBox.IsChecked == true,
    };

    private void RefreshEmpty()
    {
        EmptySites.Visibility = _sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyApps.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppsHint();
    }

    private void UpdateAppsHint()
    {
        bool only = AppsOnlyRb.IsChecked == true;
        AppsHint.Text = only
            ? (_apps.Count == 0
                ? "Список пуст — режим не применится, пока вы не добавите программы. Всё идёт через VPN, как обычно."
                : "Через VPN пойдут только эти программы (и сайты из списка «через VPN»). Остальное — напрямую.")
            : "Эти программы будут работать мимо VPN (например, игры ради меньшего пинга). Остальное — как обычно.";
    }

    private void OnAppsMode(object s, RoutedEventArgs e) => UpdateAppsHint();

    // ---- сайты ----
    private void ShowSiteError(string? text)
    {
        SiteError.Text = text ?? "";
        SiteError.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddSite(bool vpn)
    {
        var v = RouteRules.NormalizeSite(SiteBox.Text, out var err);
        if (v == null) { ShowSiteError(err); return; }
        ShowSiteError(null);
        var existing = _sites.FirstOrDefault(r => r.Value == v);
        if (existing != null)
        {
            // уже есть: просто переносим в нужный список — в двух сразу правило быть не может
            if (existing.IsVpn != vpn) { existing.IsVpn = vpn; ShowInfo($"«{v}» перенесён: {(vpn ? "через VPN" : "напрямую")}."); }
            else ShowInfo($"«{v}» уже в списке.");
        }
        else
        {
            _sites.Insert(0, new SiteRuleVm(v, vpn));
            ShowInfo(null);
        }
        SiteBox.Clear();
        SiteBox.Focus();
    }

    private void OnAddDirect(object s, RoutedEventArgs e) => AddSite(false);
    private void OnAddVpn(object s, RoutedEventArgs e) => AddSite(true);

    private void OnSiteKey(object s, KeyEventArgs e)
    {
        // Enter — «через VPN» (то, ради чего чаще всего добавляют), Ctrl+Enter — напрямую
        if (e.Key == Key.Enter) AddSite(vpn: (Keyboard.Modifiers & ModifierKeys.Control) == 0);
    }

    private void OnFlip(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: SiteRuleVm r }) r.IsVpn = !r.IsVpn;
    }

    private void OnDeleteSite(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: SiteRuleVm r }) _sites.Remove(r);
    }

    // ---- программы ----
    private void AddApp(string input)
    {
        var v = RouteRules.NormalizeApp(input, out var err);
        if (v == null) { AppError.Text = err; AppError.Visibility = Visibility.Visible; return; }
        AppError.Visibility = Visibility.Collapsed;
        if (_apps.Any(a => string.Equals(a.Name, v, StringComparison.OrdinalIgnoreCase))) ShowInfo($"«{v}» уже в списке.");
        else { _apps.Insert(0, new AppRuleVm(v)); ShowInfo(null); }
        AppBox.Clear();
    }

    private void OnAddApp(object s, RoutedEventArgs e) => AddApp(AppBox.Text);
    private void OnAppKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AddApp(AppBox.Text); }

    private void OnPickApp(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите программу" };
        if (dlg.ShowDialog(this) == true) AddApp(dlg.FileName);
    }

    private void OnDeleteApp(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: AppRuleVm a }) _apps.Remove(a);
    }

    // ---- копирование и вставка списка ----
    private void ShowInfo(string? text)
    {
        Info.Text = text ?? "";
        Info.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCopy(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(RouteRules.Export(Snapshot())); ShowInfo("Список скопирован. Вставьте его на другом компьютере кнопкой «Вставить из буфера»."); }
        catch (Exception ex) { ShowInfo("Не удалось скопировать: " + ex.Message); }
    }

    private void OnPaste(object s, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText(); } catch (Exception ex) { ShowInfo("Не удалось прочитать буфер обмена: " + ex.Message); return; }
        if (string.IsNullOrWhiteSpace(text)) { ShowInfo("В буфере обмена нет текста."); return; }
        var tmp = Snapshot();
        var r = RouteRules.Import(tmp, text);
        Load(tmp);
        string msg = $"Добавлено правил: {r.Added}" + (r.Skipped > 0 ? $", уже были: {r.Skipped}" : "");
        if (r.Errors.Count > 0) msg += $". Пропущено из-за ошибок: {r.Errors.Count} (например: {r.Errors[0]})";
        ShowInfo(msg);
    }

    // ---- сохранение ----
    private void Apply()
    {
        var snap = Snapshot();
        _state.ProxySites = snap.ProxySites;
        _state.DirectSites = snap.DirectSites;
        _state.DirectApps = snap.DirectApps;
        _state.AppsMode = snap.AppsMode;
        _state.SteamDirect = snap.SteamDirect;
    }

    private void OnSave(object s, RoutedEventArgs e) { Apply(); DialogResult = true; }

    private void OnSaveReconnect(object s, RoutedEventArgs e) { Apply(); ReconnectRequested = true; DialogResult = true; }
}
