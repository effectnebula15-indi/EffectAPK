using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EffectApk.Core;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.UI;

/// <summary>
/// Нативное окно одного Android-приложения.
/// Обычный resize держит пропорции видеопотока (перехват WM_SIZING);
/// resize с зажатым Shift — свободный, и после отпускания мыши применяет
/// новое разрешение к виртуальному Android-дисплею через adb wm size.
/// </summary>
public partial class AppHostWindow : Window
{
    private readonly ScrcpyHost _host;
    private readonly Func<int, int, Task> _applyResolutionAsync;
    private double _aspect;
    private bool _shiftResizePending;

    public AppHostWindow(
        string title,
        ImageSource? icon,
        IntPtr scrcpyHwnd,
        double aspect,
        Func<int, int, Task> applyResolutionAsync,
        Func<Task> onClosedAsync)
    {
        InitializeComponent();
        Title = title;
        if (icon != null) Icon = icon;
        _aspect = aspect;
        _applyResolutionAsync = applyResolutionAsync;

        // Стартовый размер: ~80% рабочей области по высоте, ширина — по пропорциям потока
        var workArea = SystemParameters.WorkArea;
        Height = Math.Min(workArea.Height * 0.8, 960);
        Width = Math.Max(MinWidth, Height * _aspect);

        _host = new ScrcpyHost(scrcpyHwnd);
        Root.Children.Add(_host);

        SourceInitialized += (_, _) =>
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndHook);
        Activated += (_, _) => _host.FocusChild();
        Closed += async (_, _) =>
        {
            Logger.Info($"Окно закрыто: {Title}");
            try { await onClosedAsync(); }
            catch (Exception ex) { Logger.Error("Ошибка при закрытии сессии", ex); }
        };
        Logger.Info($"Окно создано: «{title}», аспект {_aspect:F3}");
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

            Logger.Info($"Shift-resize: клиентская область {clientRect.Width}x{clientRect.Height}, применяю новое разрешение");
            await _applyResolutionAsync(clientRect.Width, clientRect.Height);
            // Дальше «обычный» resize держит уже новые пропорции
            _aspect = (double)clientRect.Width / clientRect.Height;
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось применить новое разрешение Android-дисплея", ex);
        }
    }
}
