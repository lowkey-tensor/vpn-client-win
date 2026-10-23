using System.Windows;

namespace VpnClient;

/// <summary>Размер и положение окна с учётом рабочей области экрана (на ноутбуках окно не должно уходить за край).</summary>
public static class WindowLayout
{
    public const double DefaultWidth = 460, DefaultHeight = 920;
    public const double MinWidth = 380, MinHeight = 420;
    private const double Margin = 12;   // зазор до краёв рабочей области

    /// <summary>Подбирает размер: сохранённый (или стандартный), но не больше рабочей области.</summary>
    public static (double Width, double Height) Fit(Rect work, double? savedWidth, double? savedHeight)
    {
        double w = savedWidth is > 0 ? savedWidth.Value : DefaultWidth;
        double h = savedHeight is > 0 ? savedHeight.Value : DefaultHeight;
        w = Math.Min(w, Math.Max(MinWidth, work.Width - 2 * Margin));
        h = Math.Min(h, Math.Max(MinHeight, work.Height - 2 * Margin));
        return (Math.Max(MinWidth, w), Math.Max(MinHeight, h));
    }

    /// <summary>Положение годится, если заголовок окна целиком виден на рабочей области (например, не остался на отключённом мониторе).</summary>
    public static bool IsOnScreen(Rect work, double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || left < -10000 || top < -10000) return false;
        return left >= work.Left - 8 && top >= work.Top - 8
            && left + Math.Min(width, 160) <= work.Right + 8
            && top + 40 <= work.Bottom;
    }

    public static (double Left, double Top) Center(Rect work, double width, double height) =>
        (work.Left + Math.Max(0, (work.Width - width) / 2), work.Top + Math.Max(0, (work.Height - height) / 2));
}
