using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EffectApk.Core;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.UI;

/// <summary>Снимок состояния окна для сохранения между запусками (координаты и размеры — в DIP).</summary>
public sealed record WindowSnapshot(
    int DisplayWidth, int DisplayHeight,
    double Left, double Top, double Width, double Height);

/// <summary>
/// Нативное окно одного Android-приложения. Окно и виртуальный дисплей совпадают 1:1:
/// клиентская область всегда снапится к «сетке дисплея» (кратно 8, минимум 320 px),
/// а разрешение и плотность дисплея синхронизируются точно в её пиксели — картинка
/// рендерится без масштабирования. Обычный resize держит пропорции потока (WM_SIZING),
/// Shift+resize — свободный (приложение переверстается под новые пропорции),
/// «Повернуть» в системном меню меняет портрет и альбом местами.
/// </summary>
public partial class AppHostWindow : Window
{
    private readonly ScrcpyView _view;
    private readonly Sessions.ScrcpyClient _client;
    private readonly Func<int, int, int, Task> _applyResolutionAsync; // (ширина, высота, плотность dpi)
    private readonly bool _restoredGeometry;
    private readonly DispatcherTimer _syncDebounce;
    private int _displayWidth;
    private int _displayHeight;
    private double _aspect;
    private bool _inSizeMove;
    private bool _syncing;

    public AppHostWindow(
        string title,
        ImageSource? icon,
        Sessions.ScrcpyClient client,
        int displayWidth,
        int displayHeight,
        WindowSnapshot? restoredState,
        Func<int, int, int, Task> applyResolutionAsync,
        Func<WindowSnapshot, Task> onClosedAsync)
    {
        InitializeComponent();
        Title = title;
        if (icon != null) Icon = icon;
        _client = client;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;
        _aspect = (double)displayWidth / displayHeight;
        _applyResolutionAsync = applyResolutionAsync;

        // Дебаунс синхронизации для событий вне drag-цикла (максимизация, программный resize)
        _syncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _syncDebounce.Tick += (_, _) =>
        {
            _syncDebounce.Stop();
            _ = SyncResolutionToWindowAsync();
        };

        // Здесь только позиция: точный размер клиентской области (пиксель-в-пиксель
        // с дисплеем) задаётся в OnSourceInitialized через MoveWindow — DIP-величины
        // Width/Height округляются и давали расхождение окна с дисплеем на 1-2 px.
        if (restoredState is { } saved)
        {
            _restoredGeometry = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
        }

        _view = new ScrcpyView(client);
        Root.Children.Add(_view);

        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => _view.Focus();
        Closed += async (_, _) =>
        {
            _syncDebounce.Stop();
            Logger.Info($"Окно закрыто: {Title}");
            try { await onClosedAsync(Snapshot()); }
            catch (Exception ex) { Logger.Error("Ошибка при закрытии сессии", ex); }
        };
        Logger.Info($"Окно создано: «{title}», дисплей {displayWidth}x{displayHeight}" +
                    (restoredState != null ? " (геометрия восстановлена)" : ""));
    }

    private WindowSnapshot Snapshot()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        return new WindowSnapshot(_displayWidth, _displayHeight,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndHook);

        // Пункт «Повернуть» в системном меню окна
        var sysMenu = GetSystemMenu(source.Handle, false);
        AppendMenu(sysMenu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(sysMenu, MF_STRING, (UIntPtr)SC_ROTATE, "Повернуть (портрет/альбом)");

        // Клиентская область = разрешение дисплея, пиксель-в-пиксель (до первой отрисовки)
        MoveWindowToClientSize(source.Handle, _displayWidth, _displayHeight, center: !_restoredGeometry);
    }

    /// <summary>
    /// Ставит окну точный размер клиентской области в физических пикселях (снапнутый к сетке
    /// дисплея и вписанный в рабочую область монитора) — единственный способ гарантировать
    /// совпадение с дисплеем: DIP-свойства Width/Height округляются при пересчёте в пиксели.
    /// </summary>
    private void MoveWindowToClientSize(IntPtr hwnd, int clientW, int clientH, bool center)
    {
        GetWindowRect(hwnd, out var windowRect);
        GetClientRect(hwnd, out var clientRect);
        var chromeW = windowRect.Width - clientRect.Width;
        var chromeH = windowRect.Height - clientRect.Height;
        var work = GetWorkAreaPx(hwnd);

        // Не влезает на монитор — уменьшаем с сохранением пропорций (дисплей догонит по WM_SIZE)
        var maxClientW = Math.Max(ResizePolicy.MinSide, work.Width - chromeW);
        var maxClientH = Math.Max(ResizePolicy.MinSide, work.Height - chromeH);
        if (clientW > maxClientW || clientH > maxClientH)
        {
            var fit = Math.Min((double)maxClientW / clientW, (double)maxClientH / clientH);
            clientW = ResizePolicy.SnapWindowSide(clientW * fit);
            clientH = ResizePolicy.SnapWindowSide(clientH * fit);
        }

        var w = clientW + chromeW;
        var h = clientH + chromeH;
        var x = center
            ? work.Left + Math.Max(0, (work.Width - w) / 2)
            : Math.Clamp(windowRect.Left, work.Left, Math.Max(work.Left, work.Right - w));
        var y = center
            ? work.Top + Math.Max(0, (work.Height - h) / 2)
            : Math.Clamp(windowRect.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        MoveWindow(hwnd, x, y, w, h, true);
        Logger.Info($"Окно подогнано: клиент {clientW}x{clientH} px (дисплей {_displayWidth}x{_displayHeight})");
    }

    private static RECT GetWorkAreaPx(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcWork;
        var work = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0);
        return work;
    }

    /// <summary>
    /// Пост-стартовая проверка: сверяет окно с ФАКТИЧЕСКИМ размером видеопотока, а не с нашим
    /// представлением о дисплее. WindowManager хранит wm size-override по uniqueId дисплея,
    /// и «хвост» прошлой сессии реаттачится к свежесозданному дисплею scrcpy — база при этом
    /// правильная, поэтому расхождение видно только по потоку. Лечение — принудительный wm size.
    /// </summary>
    public Task SyncResolutionToWindowIfNeededAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return Task.CompletedTask;
        GetClientRect(hwnd, out var client);
        var (width, height) = ResizePolicy.ComputeAndroidResolution(client.Width, client.Height);
        if (_client.VideoWidth == width && _client.VideoHeight == height &&
            Math.Abs(width - _displayWidth) <= 2 && Math.Abs(height - _displayHeight) <= 2)
            return Task.CompletedTask;
        Logger.Info($"Пост-стартовая проверка: поток {_client.VideoWidth}x{_client.VideoHeight}, " +
                    $"окно {client.Width}x{client.Height} — принудительная синхронизация");
        return SyncResolutionToWindowAsync(force: true);
    }

    /// <summary>
    /// Приводит разрешение и плотность Android-дисплея к текущим пикселям клиентской области.
    /// Вызывается после любого изменения размеров окна; совпадающее разрешение не переприменяет
    /// (перемещение окна и повторные WM_SIZE не дёргают приложение).
    /// </summary>
    public async Task SyncResolutionToWindowAsync(bool force = false)
    {
        if (_syncing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        GetClientRect(hwnd, out var client);
        if (client.Width < 50 || client.Height < 50) return;

        var (width, height) = ResizePolicy.ComputeAndroidResolution(client.Width, client.Height);
        if (!force && width == _displayWidth && height == _displayHeight) return;

        _syncing = true;
        try
        {
            var density = ResizePolicy.ComputeDensity(width, height);
            Logger.Info($"Синхронизация: окно {client.Width}x{client.Height} → дисплей {width}x{height} @ {density} dpi");
            await _applyResolutionAsync(width, height, density);
            (_displayWidth, _displayHeight) = (width, height);
            _aspect = (double)width / height;
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось синхронизировать разрешение с окном", ex);
        }
        finally
        {
            _syncing = false;
        }
    }

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SIZING:
                // Обычный drag держит пропорции потока, Shift — свободный; в обоих случаях
                // клиентская область снапится к сетке дисплея (кратно 8, ≥320) — после
                // отпускания дисплей получит ровно эти пиксели, совпадение 1:1
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                ConstrainSizingRect(hwnd, ref rect, wParam.ToInt32(),
                    keepAspect: (GetKeyState(VK_SHIFT) & 0x8000) == 0);
                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
                return (IntPtr)1;

            case WM_GETMINMAXINFO:
                ApplyMinTrackSize(hwnd, lParam);
                break;

            case WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                break;

            case WM_EXITSIZEMOVE:
                _inSizeMove = false;
                _ = SyncResolutionToWindowAsync();
                break;

            case WM_SIZE when !_inSizeMove &&
                              (wParam.ToInt64() == SIZE_MAXIMIZED || wParam.ToInt64() == SIZE_RESTORED):
                // Максимизация/восстановление/программный resize — без WM_EXITSIZEMOVE
                _syncDebounce.Stop();
                _syncDebounce.Start();
                break;

            case WM_SYSCOMMAND when (wParam.ToInt64() & 0xFFF0) == SC_ROTATE:
                Rotate();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Минимальный размер окна: клиентская область не меньше 320x320 px (минимум дисплея).</summary>
    private static void ApplyMinTrackSize(IntPtr hwnd, IntPtr lParam)
    {
        GetWindowRect(hwnd, out var windowRect);
        GetClientRect(hwnd, out var clientRect);
        var chromeW = windowRect.Width - clientRect.Width;
        var chromeH = windowRect.Height - clientRect.Height;
        if (clientRect.Width <= 0 || clientRect.Height <= 0) return; // окно ещё создаётся

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        info.ptMinTrackSize.X = ResizePolicy.MinSide + chromeW;
        info.ptMinTrackSize.Y = ResizePolicy.MinSide + chromeH;
        Marshal.StructureToPtr(info, lParam, false);
    }

    private void ConstrainSizingRect(IntPtr hwnd, ref RECT rect, int edge, bool keepAspect)
    {
        // Пропорции и сетка нужны клиентской области, а не всему окну — вычитаем «хром»
        GetWindowRect(hwnd, out var windowRect);
        GetClientRect(hwnd, out var clientRect);
        var chromeW = windowRect.Width - clientRect.Width;
        var chromeH = windowRect.Height - clientRect.Height;

        var clientW = Math.Max(1, rect.Width - chromeW);
        var clientH = Math.Max(1, rect.Height - chromeH);

        int snappedW, snappedH;
        if (keepAspect)
        {
            // За верх/низ ведём по высоте, иначе по ширине; вторую сторону считаем от уже
            // снапнутой ведущей — если минимум зажал результат, пересчитываем обратно
            if (edge is WMSZ_TOP or WMSZ_BOTTOM)
            {
                snappedH = ResizePolicy.SnapWindowSide(clientH);
                snappedW = ResizePolicy.SnapWindowSide(snappedH * _aspect);
                if (snappedW == ResizePolicy.MinSide)
                    snappedH = ResizePolicy.SnapWindowSide(snappedW / _aspect);
            }
            else
            {
                snappedW = ResizePolicy.SnapWindowSide(clientW);
                snappedH = ResizePolicy.SnapWindowSide(snappedW / _aspect);
                if (snappedH == ResizePolicy.MinSide)
                    snappedW = ResizePolicy.SnapWindowSide(snappedH * _aspect);
            }
        }
        else
        {
            snappedW = ResizePolicy.SnapWindowSide(clientW);
            snappedH = ResizePolicy.SnapWindowSide(clientH);
        }

        var newW = snappedW + chromeW;
        var newH = snappedH + chromeH;

        // Якорим стороны, противоположные той, за которую тянут
        switch (edge)
        {
            case WMSZ_LEFT or WMSZ_BOTTOMLEFT:
                rect.Left = rect.Right - newW;
                rect.Bottom = rect.Top + newH;
                break;
            case WMSZ_TOPLEFT:
                rect.Left = rect.Right - newW;
                rect.Top = rect.Bottom - newH;
                break;
            case WMSZ_TOP or WMSZ_TOPRIGHT:
                rect.Right = rect.Left + newW;
                rect.Top = rect.Bottom - newH;
                break;
            default: // WMSZ_RIGHT, WMSZ_BOTTOM, WMSZ_BOTTOMRIGHT
                rect.Right = rect.Left + newW;
                rect.Bottom = rect.Top + newH;
                break;
        }
    }

    /// <summary>
    /// Портрет ⇄ альбом: меняем местами пиксели клиентской области (с вписыванием в монитор);
    /// синхронизация дисплея произойдёт по WM_SIZE.
    /// </summary>
    private void Rotate()
    {
        try
        {
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            var hwnd = new WindowInteropHelper(this).Handle;
            GetClientRect(hwnd, out var client);
            Logger.Info($"Поворот окна: клиент {client.Width}x{client.Height} → {client.Height}x{client.Width}");
            _aspect = 1.0 / _aspect;
            MoveWindowToClientSize(hwnd, client.Height, client.Width, center: false);
        }
        catch (Exception ex)
        {
            Logger.Error("Поворот не удался", ex);
        }
    }
}
