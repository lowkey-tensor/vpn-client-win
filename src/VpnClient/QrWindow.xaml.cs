using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace VpnClient;

public partial class QrWindow : Window
{
    private readonly string _link;

    public QrWindow(NodeEntry node)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MainWindow.ApplyDark(this);
        _link = node.Link;
        NameText.Text = node.Name;

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(_link, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(10);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        QrImage.Source = img;
    }

    private void OnCopy(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_link); } catch { }
        ((System.Windows.Controls.Button)s).Content = "Скопировано ✓";
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();
}
