using System.Diagnostics;

namespace EffectApk.Core;

/// <summary>
/// Управляет единственным headless-инстансом Android Emulator.
/// Эмулятор стартует без окна (-no-window) и живёт в фоне, пока пользователь не выйдет из EffectAPK.
/// </summary>
public sealed class EmulatorService(AppSettings settings)
{
    private Process? _emulatorProcess;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AdbService CreateAdb() => new(
        settings.AdbPath ?? throw new InvalidOperationException("adb не найден — запустите мастер настройки."),
        settings.EmulatorSerial);

    public async Task<AdbService> EnsureRunningAsync(CancellationToken ct = default)
    {
        // Один запуск за раз: два одновременно открытых APK не должны поднять два эмулятора
        await _gate.WaitAsync(ct);
        try
        {
            var adb = CreateAdb();
            await adb.StartServerAsync();

            if (await adb.IsDeviceOnlineAsync() && await adb.IsBootCompletedAsync())
                return adb;

            if (_emulatorProcess is not { HasExited: false } && !await adb.IsDeviceOnlineAsync())
            {
                var emulatorExe = settings.EmulatorPath
                    ?? throw new InvalidOperationException("Android Emulator не найден — запустите мастер настройки.");

                // Без -no-snapshot-save: quick-boot снапшоты делают повторные старты почти мгновенными
                var args = $"-avd {settings.AvdName} -port {settings.EmulatorPort} " +
                           $"-no-window -no-boot-anim -gpu {settings.GpuMode}";
                Logger.Info("Запуск эмулятора: emulator " + args);
                _emulatorProcess = ProcessRunner.StartDetached(emulatorExe, args, line => Logger.Debug("[emu] " + line));
            }

            await adb.WaitForBootAsync(TimeSpan.FromMinutes(4), ct);
            Logger.Info("Эмулятор загружен: " + settings.EmulatorSerial);
            return adb;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await CreateAdb().KillEmulatorAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("adb emu kill не удался", ex);
        }

        try
        {
            if (_emulatorProcess is { HasExited: false } process && !process.WaitForExit(5000))
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // процесс уже завершился
        }
        _emulatorProcess = null;
    }
}
