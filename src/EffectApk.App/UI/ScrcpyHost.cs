using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EffectApk.Core;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.UI;

/// <summary>
/// HwndHost, «усыновляющий» окно scrcpy: SetParent + срезание рамок/заголовка + WS_CHILD.
/// Важно: при каждой смене разрешения видеопотока scrcpy сам вызывает SDL_SetWindowSize и
/// «сбегает» из размеров контейнера, поэтому геометрия принудительно контролируется двумя путями:
/// OnWindowPositionChanged (наши ресайзы) и сторожевой таймер (самовольные ресайзы scrcpy).
/// </summary>
public sealed class ScrcpyHost : HwndHost
{
    private readonly IntPtr _scrcpyHwnd;
    private IntPtr _container;
    private DispatcherTimer? _watchdog;
    private DateTime _lastCorrectionLog = DateTime.MinValue;

    public ScrcpyHost(IntPtr scrcpyHwnd) => _scrcpyHwnd = scrcpyHwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(
            0, "static", "", WS_CHILD | WS_VISIBLE,
            0, 0, 10, 10,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // Превращаем top-level окно scrcpy в child без собственного заголовка и рамки
        var style = GetWindowLongPtr(_scrcpyHwnd, GWL_STYLE).ToInt64();
        style &= ~(long)(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(_scrcpyHwnd, GWL_STYLE, (IntPtr)style);

        SetParent(_scrcpyHwnd, _container);
        ShowWindow(_scrcpyHwnd, SW_SHOW);

        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _watchdog.Tick += (_, _) => EnforceChildGeometry();
        _watchdog.Start();

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _watchdog?.Stop();
        _watchdog = null;
        // Отцепляем scrcpy перед уничтожением контейнера: его окном владеет процесс scrcpy
        SetParent(_scrcpyHwnd, IntPtr.Zero);
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>
    /// WPF вызывает это при каждом изменении геометрии контейнера (уже в физических пикселях),
    /// включая первый показ окна — в отличие от OnRenderSizeChanged.
    /// </summary>
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        EnforceChildGeometry();
    }

    public void FocusChild()
    {
        if (_scrcpyHwnd != IntPtr.Zero)
            SetFocus(_scrcpyHwnd);
    }

    /// <summary>Если окно scrcpy не совпадает с контейнером — возвращаем его на место.</summary>
    private void EnforceChildGeometry()
    {
        if (_scrcpyHwnd == IntPtr.Zero || _container == IntPtr.Zero) return;

        // scrcpy завершился (окно закрыто/процесс убит) — сторож больше не нужен
        if (!IsWindow(_scrcpyHwnd))
        {
            _watchdog?.Stop();
            _watchdog = null;
            return;
        }

        if (!GetClientRect(_container, out var target) || target.Width < 1 || target.Height < 1) return;

        GetWindowRect(_scrcpyHwnd, out var child);
        var origin = new POINT();
        ClientToScreen(_container, ref origin);

        if (child.Left == origin.X && child.Top == origin.Y &&
            child.Width == target.Width && child.Height == target.Height)
            return;

        if (DateTime.Now - _lastCorrectionLog > TimeSpan.FromMilliseconds(500))
        {
            _lastCorrectionLog = DateTime.Now;
            Logger.Debug($"Возвращаю scrcpy в контейнер: {child.Width}x{child.Height} → {target.Width}x{target.Height}");
        }
        MoveWindow(_scrcpyHwnd, 0, 0, target.Width, target.Height, true);
    }
}
