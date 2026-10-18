using System.Reflection;

namespace VpnClient;

/// <summary>Версия приложения: берётся из &lt;Version&gt; в VpnClient.csproj (4.3.0 → «4.3», 4.3.1 → «4.3.1»).</summary>
public static class AppInfo
{
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }
}
