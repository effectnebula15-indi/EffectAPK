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
/// Нативное окно одного Android-приложения.
/// Разрешение и плотность виртуального дисплея всегда синхронизируются с окном (рендер 1:1):
/// обычный resize держит пропорции потока (перехват WM_SIZING) — «зум» без размытия;
/// Shift+resize — свободный, приложение переверстается под новые пропорции;
/// «Повернуть» в системном меню меняет портрет и альбом местами.
/// </summary>
public partial class AppHostWindow : Window
{
    private readonly ScrcpyView _view;
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

        if (restoredState is { } saved)
        {
            _restoredGeometry = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
        }
        else
        {
            // Стартовый размер: ~80% рабочей области по высоте; ширину доводим до
            // пропорций клиентской области в OnSourceInitialized (когда известен «хром»)
            var workArea = SystemParameters.WorkArea;
            Height = Math.Min(workArea.Height * 0.8, 960);
            Width = Math.Max(MinWidth, Height * _aspect);
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

        // Доводим ширину так, чтобы пропорции держала клиентская область, а не окно с рамками
        if (!_restoredGeometry)
        {
            GetWindowRect(source.Handle, out var windowRect);
            GetClientRect(source.Handle, out var clientRect);
            var scale = source.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var chromeW = (windowRect.Width - clientRect.Width) / scale;
            var chromeH = (windowRect.Height - clientRect.Height) / scale;
            Width = (Height - chromeH) * _aspect + chromeW;
        }
    }

    /// <summary>
    /// Синхронизирует только если текущее разрешение дисплея заметно расходится с окном
    /// (порог 2 px). Используется при старте: если окно восстановилось ровно в стартовом
    /// размере дисплея — приложение не тревожим вовсе.
    /// </summary>
    public Task SyncResolutionToWindowIfNeededAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return Task.CompletedTask;
        GetClientRect(hwnd, out var client);
        var (width, height) = ResizePolicy.ComputeAndroidResolution(client.Width, client.Height);
        if (Math.Abs(width - _displayWidth) <= 2 && Math.Abs(height - _displayHeight) <= 2)
            return Task.CompletedTask;
        return SyncResolutionToWindowAsync();
    }

    /// <summary>
    /// Приводит разрешение и плотность Android-дисплея к текущим пикселям клиентской области.
    /// Вызывается после любого изменения размеров окна.
    /// </summary>
    public async Task SyncResolutionToWindowAsync()
    {
        if (_syncing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        GetClientRect(hwnd, out var client);
        if (client.Width < 50 || client.Height < 50) return;

        _syncing = true;
        try
        {
            var (width, height) = ResizePolicy.ComputeAndroidResolution(client.Width, client.Height);
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
                // Shift — свободные пропорции (реflow после отпускания), иначе держим аспект потока
                if ((GetKeyState(VK_SHIFT) & 0x8000) == 0)
                {
                    var rect = Marshal.PtrToStructure<RECT>(lParam);
                    ConstrainToAspect(hwnd, ref rect, wParam.ToInt32());
                    Marshal.StructureToPtr(rect, lParam, false);
                    handled = true;
                    return (IntPtr)1;
                }
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

    private void ConstrainToAspect(IntPtr hwnd, ref RECT rect, int edge)
    {
        // Пропорции нужны клиентской области, а не всему окну — вычитаем «хром» (заголовок, рамки)
        GetWindowRect(hwnd, out var windowRect);
        GetClientRect(hwnd, out var clientRect);
        var chromeW = windowRect.Width - clientRect.Width;
        var chromeH = windowRect.Height - clientRect.Height;

        var clientW = Math.Max(1, rect.Width - chromeW);
        var clientH = Math.Max(1, rect.Height - chromeH);

        // За боковые края и углы ведём по ширине, за верх/низ — по высоте
        if (edge is WMSZ_TOP or WMSZ_BOTTOM)
            clientW = (int)Math.Round(clientH * _aspect);
        else
            clientH = (int)Math.Round(clientW / _aspect);

        var newW = clientW + chromeW;
        var newH = clientH + chromeH;

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

    /// <summary>Портрет ⇄ альбом: меняем местами клиентские размеры окна; синхронизация дисплея произойдёт по WM_SIZE.</summary>
    private void Rotate()
    {
        try
        {
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            Logger.Info($"Поворот окна: {_displayWidth}x{_displayHeight} → {_displayHeight}x{_displayWidth}");
            _aspect = 1.0 / _aspect;

            var chromeW = ActualWidth - _view.ActualWidth;
            var chromeH = ActualHeight - _view.ActualHeight;
            var newClientW = _view.ActualHeight;
            var newClientH = _view.ActualWidth;
            var workArea = SystemParameters.WorkArea;
            var fit = Math.Min(1.0, Math.Min(
                (workArea.Width - chromeW) / newClientW,
                (workArea.Height - chromeH) / newClientH));
            Width = newClientW * fit + chromeW;
            Height = newClientH * fit + chromeH;
            if (Left + Width > workArea.Right) Left = Math.Max(workArea.Left, workArea.Right - Width);
            if (Top + Height > workArea.Bottom) Top = Math.Max(workArea.Top, workArea.Bottom - Height);
        }
        catch (Exception ex)
        {
            Logger.Error("Поворот не удался", ex);
        }
    }
}
