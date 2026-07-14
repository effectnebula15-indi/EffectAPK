using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static EffectApk.Interop.NativeMethods;

namespace EffectApk.UI;

/// <summary>
/// HwndHost, «усыновляющий» окно scrcpy: SetParent + срезание рамок/заголовка + WS_CHILD.
/// scrcpy продолжает сам рендерить видео и обрабатывать ввод — мы лишь даём ему место в нашем окне.
/// </summary>
public sealed class ScrcpyHost : HwndHost
{
    private readonly IntPtr _scrcpyHwnd;
    private IntPtr _container;

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

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Отцепляем scrcpy перед уничтожением контейнера: его окном владеет процесс scrcpy
        SetParent(_scrcpyHwnd, IntPtr.Zero);
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>
    /// WPF вызывает это при каждом перемещении/ресайзе контейнера, причём rcBoundingBox —
    /// уже в физических пикселях. В отличие от OnRenderSizeChanged, срабатывает и при первом
    /// показе окна, поэтому scrcpy всегда растянут на всю клиентскую область.
    /// </summary>
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_scrcpyHwnd == IntPtr.Zero) return;

        var width = Math.Max(1, (int)Math.Round(rcBoundingBox.Width));
        var height = Math.Max(1, (int)Math.Round(rcBoundingBox.Height));
        MoveWindow(_scrcpyHwnd, 0, 0, width, height, true);
    }

    public void FocusChild()
    {
        if (_scrcpyHwnd != IntPtr.Zero)
            SetFocus(_scrcpyHwnd);
    }
}
