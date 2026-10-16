using System.Windows;

namespace VpnClient;

public partial class RoutesWindow : Window
{
    private readonly State _state;

    public RoutesWindow(State state)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.ApplyDark(this);
        _state = state;
        AppsBox.Text = string.Join(Environment.NewLine, state.DirectApps);
        DirectBox.Text = string.Join(Environment.NewLine, state.DirectSites);
        ProxyBox.Text = string.Join(Environment.NewLine, state.ProxySites);
    }

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void OnPickApp(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Программа, которая пойдёт мимо VPN" };
        if (dlg.ShowDialog(this) != true) return;
        string name = System.IO.Path.GetFileName(dlg.FileName);
        if (!Lines(AppsBox.Text).Contains(name, StringComparer.OrdinalIgnoreCase))
            AppsBox.Text = (AppsBox.Text.TrimEnd() + Environment.NewLine + name).Trim();
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        _state.DirectApps = BuildOptions.CleanApps(Lines(AppsBox.Text)).ToList();
        _state.DirectSites = BuildOptions.CleanSites(Lines(DirectBox.Text)).ToList();
        _state.ProxySites = BuildOptions.CleanSites(Lines(ProxyBox.Text)).ToList();
        DialogResult = true;
    }
}
