using System.Diagnostics;

namespace VpnClient;

/// <summary>Автозапуск через Планировщик заданий: старт при входе в Windows с правами администратора, без окна UAC.</summary>
public static class AutoStart
{
    private const string TaskName = "VpnClient";

    public static bool IsEnabled()
    {
        var r = Run("schtasks.exe", $"/Query /TN {TaskName}");
        return r == 0;
    }

    public static bool Enable()
    {
        string exe = Environment.ProcessPath!.Replace("'", "''");
        string ps =
            $"$a=New-ScheduledTaskAction -Execute '{exe}' -Argument '--autoconnect';" +
            "$t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME;" +
            "$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive;" +
            "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero);" +
            $"Register-ScheduledTask -TaskName {TaskName} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
        return Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{ps}\"") == 0;
    }

    public static bool Disable() => Run("schtasks.exe", $"/Delete /TN {TaskName} /F") == 0;

    private static int Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
