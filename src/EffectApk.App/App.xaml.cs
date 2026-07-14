using System.IO;
using System.Windows;
using EffectApk.Core;
using EffectApk.Sessions;
using EffectApk.UI;

namespace EffectApk;

/// <summary>
/// Оркестратор: single-instance, трей, мастер настройки и жизненный цикл сессий
/// «.apk → установка → scrcpy → окно». Главного окна у приложения нет.
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppSettings _settings = null!;
    private EmulatorService _emulator = null!;
    private TrayIconService? _tray;
    private readonly Dictionary<string, AppSession> _sessions = new(); // ключ — имя пакета
    private readonly CancellationTokenSource _cts = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Глобальные обработчики: любое необработанное исключение попадает в лог
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Необработанное исключение в UI-потоке", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Необработанное исключение AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Необработанное исключение фоновой задачи", args.Exception);
            args.SetObserved();
        };

        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        Logger.Info($"EffectAPK {typeof(App).Assembly.GetName().Version} запущен: " +
                    $"pid={Environment.ProcessId}, args=[{string.Join(' ', e.Args)}], {Environment.OSVersion}");

        var apks = e.Args
            .Where(a => a.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            .Select(Path.GetFullPath)
            .ToList();

        _instanceMutex = SingleInstance.TryAcquire();
        if (_instanceMutex == null)
        {
            // Уже работает основной экземпляр — передаём ему файлы и тихо выходим
            try { SingleInstance.SendToPrimary(apks); }
            catch (Exception ex) { Logger.Error("Не удалось передать файл основному экземпляру", ex); }
            Shutdown();
            return;
        }

        try
        {
            _settings = AppSettings.Load();
            _emulator = new EmulatorService(_settings);
            _tray = new TrayIconService(
                associateApk: () =>
                {
                    FileAssociationService.RegisterForCurrentUser();
                    _tray!.Balloon(".apk-файлы теперь открываются через EffectAPK.");
                },
                openLog: Logger.OpenLogFolder,
                stopRuntime: () => _ = StopRuntimeAsync(),
                exit: () => _ = ExitAsync(),
                isAutoStartEnabled: AutoStartService.IsEnabled,
                setAutoStart: AutoStartService.SetEnabled);

            _ = SingleInstance.RunServerAsync(
                path => Dispatcher.InvokeAsync(() => _ = OpenApkSafeAsync(path)),
                _cts.Token);

            // При автозагрузке (--background) мастер не показываем: тихо сидим в трее
            if (!background &&
                (!_settings.SetupCompleted || new RuntimeBootstrapper(_settings).Validate().Count > 0))
            {
                var completed = new SetupWizardWindow(_settings).ShowDialog();
                if (completed != true)
                {
                    MessageBox.Show(
                        "Без настроенного Android-рантайма EffectAPK не может открывать .apk.\n" +
                        "Мастер запустится снова при следующем открытии .apk-файла.",
                        "EffectAPK", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (apks.Count > 0)
                    {
                        await ExitAsync();
                        return;
                    }
                }
            }

            if (!FileAssociationService.IsRegistered())
                FileAssociationService.RegisterForCurrentUser();

            // Автозагрузка включается один раз после успешной настройки; дальше решает пользователь
            if (_settings.SetupCompleted && !_settings.AutoStartConfigured)
            {
                AutoStartService.SetEnabled(true);
                _settings.AutoStartConfigured = true;
                _settings.Save();
            }

            if (apks.Count == 0 && !background)
                _tray.Balloon("EffectAPK работает в фоне. Дважды кликните по .apk-файлу, чтобы запустить приложение.");

            foreach (var apk in apks)
                await OpenApkSafeAsync(apk);
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка запуска EffectAPK", ex);
            MessageBox.Show("EffectAPK не смог запуститься: " + ex.Message,
                "EffectAPK", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private async Task OpenApkSafeAsync(string path)
    {
        try
        {
            await OpenApkAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось открыть {path}", ex);
            MessageBox.Show(
                $"Не удалось открыть «{Path.GetFileName(path)}»:\n{ex.Message}",
                "EffectAPK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OpenApkAsync(string path)
    {
        var aapt2 = _settings.Aapt2Path
            ?? throw new InvalidOperationException("aapt2 не найден — завершите мастер настройки.");
        var apk = await new ApkInspector(aapt2).InspectAsync(path);
        Logger.Info($"Открытие {apk.PackageName} v{apk.VersionCode} ({path})");

        // Повторное открытие того же пакета — просто поднимаем существующее окно
        if (_sessions.TryGetValue(apk.PackageName, out var existing))
        {
            if (!existing.Scrcpy.HasExited)
            {
                existing.Window.Activate();
                return;
            }
            _sessions.Remove(apk.PackageName);
        }

        _tray?.Balloon($"Запуск «{apk.Label}»… Холодный старт эмулятора может занять до минуты.");
        var adb = await _emulator.EnsureRunningAsync(_cts.Token);

        // Установка только если versionCode изменился (или пакета ещё нет)
        var installedVersion = await adb.GetInstalledVersionCodeAsync(apk.PackageName);
        if (installedVersion != apk.VersionCode)
            await adb.InstallAsync(path);

        var scrcpy = await ScrcpySession.StartAsync(_settings, apk.PackageName, adb, _cts.Token);

        var window = new AppHostWindow(
            title: apk.Label,
            icon: ApkInspector.TryExtractIcon(apk),
            scrcpyHwnd: scrcpy.WindowHandle,
            aspect: (double)_settings.DisplayWidth / _settings.DisplayHeight,
            applyResolutionAsync: async (clientW, clientH) =>
            {
                if (scrcpy.DisplayId is not { } displayId) return;
                var (width, height) = ResizePolicy.ComputeAndroidResolution(clientW, clientH);
                await adb.SetDisplaySizeAsync(displayId, width, height);
            },
            onClosedAsync: async () =>
            {
                _sessions.Remove(apk.PackageName);
                scrcpy.Stop();
                try { await adb.ForceStopAsync(apk.PackageName); }
                catch (Exception ex) { Logger.Error("force-stop не удался", ex); }
            });

        _sessions[apk.PackageName] = new AppSession(apk, scrcpy, window);

        // scrcpy умер (эмулятор остановлен, поток оборвался) — закрываем осиротевшее окно
        scrcpy.Exited += () => Dispatcher.InvokeAsync(() =>
        {
            if (_sessions.TryGetValue(apk.PackageName, out var s) && s.Scrcpy == scrcpy)
                s.Window.Close();
        });

        window.Show();
        window.Activate();
    }

    private async Task StopRuntimeAsync()
    {
        foreach (var session in _sessions.Values.ToList())
            session.Window.Close();
        await _emulator.StopAsync();
        _tray?.Balloon("Android-рантайм остановлен.");
    }

    private async Task ExitAsync()
    {
        try
        {
            foreach (var session in _sessions.Values.ToList())
                session.Window.Close();
            await _emulator.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка при завершении", ex);
        }
        _cts.Cancel();
        _tray?.Dispose();
        Shutdown();
    }
}
