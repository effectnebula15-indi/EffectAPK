using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EffectApk.Core;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.UI;

/// <summary>Снимок состояния окна для сохранения между запусками (координаты и размеры — в DIP).</summary>
public sealed record WindowSnapshot(
    int DisplayWidth, int DisplayHeight,
    double Left, double Top, double Width, double Height);

/// <summary>
/// Нативное окно одного Android-приложения.
/// Обычный resize держит пропорции видеопотока (перехват WM_SIZING);
/// resize с зажатым Shift — свободный, после отпускания мыши применяет новое
/// разрешение к виртуальному Android-дисплею; пункт «Повернуть» в системном
/// меню (клик по заголовку правой кнопкой) меняет портрет и альбом местами.
/// </summary>
public partial class AppHostWindow : Window
{
    private readonly ScrcpyHost _host;
    private readonly Func<int, int, Task> _applyResolutionAsync; // (ширина, высота) Android-дисплея
    private readonly bool _restoredGeometry;
    private int _displayWidth;
    private int _displayHeight;
    private double _aspect;
    private bool _shiftResizePending;
    private bool _rotating;

    public AppHostWindow(
        string title,
        ImageSource? icon,
        IntPtr scrcpyHwnd,
        int displayWidth,
        int displayHeight,
        WindowSnapshot? restoredState,
        Func<int, int, Task> applyResolutionAsync,
        Func<WindowSnapshot, Task> onClosedAsync)
    {
        InitializeComponent();
        Title = title;
        if (icon != null) Icon = icon;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;
        _aspect = (double)displayWidth / displayHeight;
        _applyResolutionAsync = applyResolutionAsync;

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

        _host = new ScrcpyHost(scrcpyHwnd);
        Root.Children.Add(_host);

        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => _host.FocusChild();
        Closed += async (_, _) =>
        {
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

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SIZING:
                var shiftDown = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                if (shiftDown)
                {
                    // Свободный resize; новое разрешение применим, когда пользователь отпустит мышь
                    _shiftResizePending = true;
                }
                else
                {
                    var rect = Marshal.PtrToStructure<RECT>(lParam);
                    ConstrainToAspect(hwnd, ref rect, wParam.ToInt32());
                    Marshal.StructureToPtr(rect, lParam, false);
                    handled = true;
                    return (IntPtr)1;
                }
                break;

            case WM_EXITSIZEMOVE when _shiftResizePending:
                _shiftResizePending = false;
                _ = ApplyShiftResizeAsync(hwnd);
                break;

            case WM_SYSCOMMAND when (wParam.ToInt64() & 0xFFF0) == SC_ROTATE:
                _ = RotateAsync();
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

    private async Task ApplyShiftResizeAsync(IntPtr hwnd)
    {
        try
        {
            GetClientRect(hwnd, out var clientRect);
            if (clientRect.Width < 50 || clientRect.Height < 50) return;

            var (width, height) = ResizePolicy.ComputeAndroidResolution(clientRect.Width, clientRect.Height);
            Logger.Info($"Shift-resize: клиент {clientRect.Width}x{clientRect.Height} → дисплей {width}x{height}");
            await _applyResolutionAsync(width, height);
            (_displayWidth, _displayHeight) = (width, height);
            _aspect = (double)width / height;
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось применить новое разрешение Android-дисплея", ex);
        }
    }

    private async Task RotateAsync()
    {
        if (_rotating) return;
        _rotating = true;
        try
        {
            var (newWidth, newHeight) = (_displayHeight, _displayWidth);
            Logger.Info($"Поворот: {_displayWidth}x{_displayHeight} → {newWidth}x{newHeight}");
            await _applyResolutionAsync(newWidth, newHeight);
            (_displayWidth, _displayHeight) = (newWidth, newHeight);
            _aspect = (double)newWidth / newHeight;

            // Меняем местами клиентские размеры окна, не вылезая за рабочую область
            var chromeW = ActualWidth - _host.ActualWidth;
            var chromeH = ActualHeight - _host.ActualHeight;
            var newClientW = _host.ActualHeight;
            var newClientH = _host.ActualWidth;
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
        finally
        {
            _rotating = false;
        }
    }
}
