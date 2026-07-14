using System.Diagnostics;
using System.IO;

namespace EffectApk.Core;

public static class Logger
{
    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EffectApk", "logs");

    private static readonly string LogFile = Path.Combine(LogDir, "effectapk.log");
    private static readonly object Gate = new();

    static Logger()
    {
        Directory.CreateDirectory(LogDir);
        try
        {
            // Ротация при старте: лог свыше 5 МБ уезжает в effectapk.prev.log
            var current = new FileInfo(LogFile);
            if (current.Exists && current.Length > 5 * 1024 * 1024)
            {
                var previous = Path.Combine(LogDir, "effectapk.prev.log");
                File.Delete(previous);
                File.Move(LogFile, previous);
            }
        }
        catch
        {
            // ротация не критична
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Debug(string message) => Write("DBG ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    public static void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Error("Не удалось открыть папку логов", ex); }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // логирование не должно ронять приложение
        }
    }
}
