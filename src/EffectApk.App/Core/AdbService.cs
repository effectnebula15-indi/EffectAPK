using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EffectApk.Core;

/// <summary>Обёртка над adb.exe, привязанная к конкретному устройству (serial эмулятора).</summary>
public sealed class AdbService(string adbPath, string serial)
{
    public string Serial => serial;

    private Task<ProcessResult> AdbAsync(string args, TimeSpan? timeout = null) =>
        ProcessRunner.RunAsync(adbPath, args, timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>Команда для конкретного устройства (adb -s serial …).</summary>
    public Task<ProcessResult> DeviceAsync(string args, TimeSpan? timeout = null) =>
        AdbAsync($"-s {serial} {args}", timeout);

    public Task StartServerAsync() => AdbAsync("start-server", TimeSpan.FromSeconds(20));

    public async Task<bool> IsDeviceOnlineAsync()
    {
        var result = await AdbAsync("devices");
        return result.StdOut
            .Split('\n')
            .Any(line => line.TrimEnd().StartsWith(serial) && line.Contains("\tdevice"));
    }

    public async Task<bool> IsBootCompletedAsync()
    {
        var result = await DeviceAsync("shell getprop sys.boot_completed", TimeSpan.FromSeconds(10));
        return result.Ok && result.StdOut.Trim() == "1";
    }

    public async Task WaitForBootAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await IsBootCompletedAsync()) return;
            }
            catch (Exception ex)
            {
                Logger.Debug("Ожидание загрузки эмулятора: " + ex.Message);
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("Android-эмулятор не загрузился за отведённое время. Проверьте лог эмулятора.");
    }

    public async Task InstallAsync(string apkPath)
    {
        // -r: переустановка с сохранением данных, -t: разрешить testOnly-сборки
        var result = await DeviceAsync($"install -r -t \"{apkPath}\"", TimeSpan.FromMinutes(5));
        if (!result.Ok || !result.StdOut.Contains("Success"))
            throw new InvalidOperationException($"adb install не удался:\n{result.StdOut}\n{result.StdErr}");
    }

    /// <summary>versionCode установленного пакета или null, если пакет не установлен.</summary>
    public async Task<long?> GetInstalledVersionCodeAsync(string packageName)
    {
        var result = await DeviceAsync($"shell dumpsys package {packageName}");
        var match = Regex.Match(result.StdOut, @"versionCode=(\d+)");
        return match.Success ? long.Parse(match.Groups[1].Value) : null;
    }

    public Task ForceStopAsync(string packageName) =>
        DeviceAsync($"shell am force-stop {packageName}");

    /// <summary>Меняет разрешение логического дисплея. Флаг -d (выбор дисплея) доступен с Android 13; наш образ — API 34.</summary>
    public async Task SetDisplaySizeAsync(int displayId, int width, int height)
    {
        var result = await DeviceAsync($"shell wm size {width}x{height} -d {displayId}");
        if (result.Ok)
            Logger.Info($"Дисплей {displayId} переключён на {width}x{height}");
        else
            Logger.Error($"wm size {width}x{height} -d {displayId} не удался: {result.StdErr} {result.StdOut}");
    }

    /// <summary>
    /// Ищет id виртуального дисплея, созданного scrcpy, в выводе dumpsys display.
    /// Эвристика: в блоке логического дисплея строка mDisplayId=N встречается раньше упоминания «scrcpy».
    /// </summary>
    public async Task<int?> FindScrcpyDisplayIdAsync()
    {
        var result = await DeviceAsync("shell dumpsys display", TimeSpan.FromSeconds(15));
        int? lastSeenId = null;
        foreach (var line in result.StdOut.Split('\n'))
        {
            var idMatch = Regex.Match(line, @"mDisplayId=(\d+)");
            if (idMatch.Success)
                lastSeenId = int.Parse(idMatch.Groups[1].Value);

            if (lastSeenId is int id and not 0 &&
                line.Contains("scrcpy", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    public Task KillEmulatorAsync() => DeviceAsync("emu kill", TimeSpan.FromSeconds(15));
}
