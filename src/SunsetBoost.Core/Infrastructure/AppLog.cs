using System.Text;

namespace SunsetBoost.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Registro simples do que o app faz (um arquivo por dia na pasta logs de <see cref="AppPaths"/>:
/// %ProgramData%\SunsetBoost\logs com admin, %LOCALAPPDATA%\SunsetBoost\logs sem).
/// Sem bibliotecas externas pra manter o consumo de RAM baixo.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static readonly List<Action<LogLevel, string>> Listeners = new();

    public static void Configure(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        lock (Gate) _dir = logsDir;
        CleanupOldLogs(logsDir, keepDays: 14);
    }

    /// <summary>Permite à interface/CLI acompanhar o log ao vivo.</summary>
    public static void Subscribe(Action<LogLevel, string> listener)
    {
        lock (Gate) Listeners.Add(listener);
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant(),-5}] {message}";
        Action<LogLevel, string>[] listeners;
        lock (Gate)
        {
            if (_dir is not null)
            {
                try
                {
                    File.AppendAllText(Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                        line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* log nunca pode derrubar o app */ }
            }
            listeners = Listeners.ToArray();
        }
        foreach (var l in listeners)
        {
            try { l(level, message); } catch { }
        }
    }

    private static void CleanupOldLogs(string dir, int keepDays)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch { }
    }
}
