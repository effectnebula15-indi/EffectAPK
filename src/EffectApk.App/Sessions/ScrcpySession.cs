using System.Diagnostics;
using System.Text.RegularExpressions;
using EffectApk.Core;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.Sessions;

/// <summary>
/// Один запущенный экземпляр scrcpy = одно Android-приложение на собственном виртуальном дисплее.
/// --new-display создаёт дисплей без статус-бара и навигации Android, --start-app запускает на нём пакет.
/// Окно scrcpy находится по уникальному заголовку и затем встраивается в наше WPF-окно (см. ScrcpyHost).
/// </summary>
public sealed class ScrcpySession
{
    private readonly Queue<string> _recentOutput = new();
    private Process? _process;

    /// <summary>Уникальный заголовок, по которому FindWindow отличит именно это окно scrcpy.</summary>
    public string WindowTitle { get; } = "EffectApk_" + Guid.NewGuid().ToString("N")[..8];

    public IntPtr WindowHandle { get; private set; }

    /// <summary>Id виртуального дисплея на стороне Android — нужен для Shift+resize (wm size -d).</summary>
    public int? DisplayId { get; private set; }

    public event Action? Exited;

    public bool HasExited => _process?.HasExited ?? true;

    public static async Task<ScrcpySession> StartAsync(
        AppSettings settings, string packageName, AdbService adb,
        int displayWidth, int displayHeight, CancellationToken ct)
    {
        var scrcpyExe = settings.ScrcpyExe
            ?? throw new InvalidOperationException("scrcpy не найден — запустите мастер настройки.");

        var session = new ScrcpySession();
        var displayIdFromLog = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var args = string.Join(' ',
            "-s", settings.EmulatorSerial,
            $"--new-display={displayWidth}x{displayHeight}/{settings.DisplayDpi}",
            $"--start-app={packageName}",
            $"--window-title={session.WindowTitle}",
            "--window-borderless");

        session._process = ProcessRunner.StartDetached(scrcpyExe, args, line =>
        {
            lock (session._recentOutput)
            {
                session._recentOutput.Enqueue(line);
                if (session._recentOutput.Count > 40) session._recentOutput.Dequeue();
            }
            Logger.Debug("[scrcpy] " + line);

            // scrcpy пишет в лог id созданного виртуального дисплея, например: «New display: ... (id=2)»
            var match = Regex.Match(line, @"[Dd]isplay.*?id[=:\s]+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && id != 0)
                displayIdFromLog.TrySetResult(id);
        });
        session._process.Exited += (_, _) => session.Exited?.Invoke();

        session.WindowHandle = await session.WaitForWindowAsync(TimeSpan.FromSeconds(25), ct);

        // display id: сначала из лога scrcpy, при неудаче — эвристика по dumpsys display
        var logTask = displayIdFromLog.Task;
        var finished = await Task.WhenAny(logTask, Task.Delay(3000, ct));
        session.DisplayId = finished == logTask ? logTask.Result : await adb.FindScrcpyDisplayIdAsync();

        Logger.Info($"scrcpy запущен: pkg={packageName}, displayId={session.DisplayId?.ToString() ?? "не определён"}");
        return session;
    }

    private async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_process!.HasExited)
                throw new InvalidOperationException("scrcpy завершился, не создав окно:\n" + RecentOutput());

            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            await Task.Delay(100, ct);
        }
        throw new TimeoutException("Окно scrcpy не появилось за отведённое время.\n" + RecentOutput());
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
            return string.Join('\n', _recentOutput);
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось остановить scrcpy", ex);
        }
    }
}
