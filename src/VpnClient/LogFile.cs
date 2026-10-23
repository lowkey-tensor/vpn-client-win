using System.IO;

namespace VpnClient;

/// <summary>Журнал в файл по дням: %APPDATA%\VpnClient\logs\ГГГГ-ММ-ДД.log, хранится 7 дней.</summary>
public static class LogFile
{
    public static readonly string Dir = Path.Combine(Store.Dir, "logs");
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>Последние n строк сегодняшнего журнала (читаем так, чтобы не мешать записи).</summary>
    public static List<string> Tail(int n)
    {
        var lines = new List<string>();
        try
        {
            string path = Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(path)) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            var q = new Queue<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) { q.Enqueue(l); if (q.Count > n) q.Dequeue(); }
            lines.AddRange(q);
        }
        catch { }
        return lines;
    }

    /// <summary>Старые журналы клиента (старше 7 дней) больше не нужны.</summary>
    public static void Rotate()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "*.log"))
                if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(7))
                    File.Delete(f);
        }
        catch { }
    }
}
